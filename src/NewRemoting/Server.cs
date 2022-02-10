﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Formatters.Binary;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Castle.Components.DictionaryAdapter;
using Castle.Core.Internal;
using Castle.DynamicProxy;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

#pragma warning disable SYSLIB0011
namespace NewRemoting
{
	/// <summary>
	/// Create a remoting server.
	/// A remoting server is listening on a network port for commands from other processes and computers.
	/// It is intended behavior that this allows a remote process to execute arbitrary code within this process, including
	/// code that is uploaded to the server from the client. No security checks nor authentication is currently used,
	/// so USE ONLY IN TRUSTED NETWORKS!
	/// </summary>
	public sealed class Server : IDisposable
	{
		public const string ServerExecutableName = "RemotingServer.exe";
		private const string RuntimeVersionRegex = "(\\d+\\.\\d)";
		internal const int AuthenticationSucceededToken = 567223;

		private readonly List<ThreadData> _threads;
		private readonly ProxyGenerator _proxyGenerator;
		private readonly CancellationTokenSource _terminatingSource = new CancellationTokenSource();
		private readonly object _channelWriterLock = new object();
		private readonly ConcurrentDictionary<string, Assembly?> _knownAssemblies = new();
		private readonly InstanceManager _instanceManager;
		private readonly MessageHandler _messageHandler;
		private readonly FormatterFactory _formatterFactory;

		/// <summary>
		/// This contains a (typically small) queue of streams that will be used for the reverse communication.
		/// The list is emptied by OpenReverseChannel commands.
		/// </summary>
		private readonly ConcurrentDictionary<int, Stream> _clientStreamsExpectingUse;

		private Thread? _mainThread;
		private int _networkPort;
		private TcpListener? _listener;
		private bool _threadRunning;

		/// <summary>
		/// This contains the stream that the client class has already opened for its own server.
		/// If this is non-null, this server instance lives within the client.
		/// </summary>
		private Stream? _preopenedStream;

		/// <summary>
		/// This is the interceptor list for calls from the server to the client using a server-side proxy (i.e. an interface registered for a callback)
		/// There is one instance per client.
		/// </summary>
		private Dictionary<string, ClientSideInterceptor> _serverInterceptorForCallbacks;

		/// <summary>
		/// Create a remoting server instance. Other processes (local or remote) will be able to perform remote calls to this process.
		/// Start <see cref="StartListening"/> to actually start the server.
		/// </summary>
		/// <param name="networkPort">Network port to open server on</param>
		/// <param name="logger">Optional logger instance, for debugging purposes</param>
		public Server(int networkPort, ILogger? logger = null)
		{
			Logger = logger ?? NullLogger.Instance;
			_networkPort = networkPort;
			_threads = new();
			_proxyGenerator = new ProxyGenerator(new DefaultProxyBuilder());
			_preopenedStream = null;
			_clientStreamsExpectingUse = new();
			_serverInterceptorForCallbacks = new();

			_instanceManager = new InstanceManager(_proxyGenerator, Logger);
			_formatterFactory = new FormatterFactory(_instanceManager);
			KillProcessWhenChannelDisconnected = false;

			// This instance will only be finally initialized once the return channel is opened
			_messageHandler = new MessageHandler(_instanceManager, _formatterFactory);
			AppDomain.CurrentDomain.AssemblyResolve += AssemblyResolver;
			RegisterStandardServices();
		}

		/// <summary>
		/// This ctor is used if this server runs on the client side (for the return channel). The actual data store is the local client instance.
		/// </summary>
		internal Server(Stream preopenedStream, MessageHandler messageHandler, ClientSideInterceptor localInterceptor, ILogger? logger = null)
		{
			_networkPort = 0;
			Logger = logger ?? NullLogger.Instance;
			_preopenedStream = preopenedStream;
			_clientStreamsExpectingUse = new(); // Shall not be used in this case
			_messageHandler = messageHandler;
			_instanceManager = messageHandler.InstanceManager;

			_threads = new();
			_formatterFactory = new FormatterFactory(_instanceManager);
			_proxyGenerator = new ProxyGenerator(new DefaultProxyBuilder());
			_serverInterceptorForCallbacks = new Dictionary<string, ClientSideInterceptor>() { { localInterceptor.OtherSideInstanceId, localInterceptor } };

			// We need the resolver also on the client side, since the server might request the client to load an assembly
			AppDomain.CurrentDomain.AssemblyResolve += AssemblyResolver;
		}

		/// <summary>
		/// If true, the server process is killed when the client disconnects unexpectedly.
		/// Use with caution, especially if not running as standalone server.
		/// </summary>
		public bool KillProcessWhenChannelDisconnected
		{
			get;
			set;
		}

		public bool IsRunning
		{
			get
			{
				return _mainThread != null && _mainThread.IsAlive;
			}
		}

		public ILogger Logger { get; }
		public int NetworkPort => _networkPort;

		public static Process StartLocalServerProcess()
		{
			return Process.Start("RemotingServer.exe");
		}

		private void RegisterStandardServices()
		{
			// Register standard services
			if (ServiceContainer.GetService<IRemoteServerService>() == null)
			{
				ServiceContainer.AddService(typeof(IRemoteServerService), new RemoteServerService(this, Logger));
			}
		}

		private Assembly? AssemblyResolver(object? sender, ResolveEventArgs args)
		{
			Logger.Log(LogLevel.Debug, $"Attempting to resolve {args.Name} from {args.RequestingAssembly}");
			if (args.RequestingAssembly == null)
			{
				return null;
			}

			bool found = _knownAssemblies.TryGetValue(args.Name, out var a);
			if (found && a != null)
			{
				return a;
			}

			_knownAssemblies.TryAdd(args.Name, null);
			if (!found)
			{
				AssemblyName name = new AssemblyName(args.Name);
				// Don't try this twice with the same assembly - causes a stack overflow
				try
				{
					// Try using the default context
					Assembly loaded = Assembly.Load(name);
					_knownAssemblies.AddOrUpdate(args.Name, loaded, (s, assembly1) => loaded);
					return loaded;
				}
				catch (Exception x)
				{
					Logger.Log(LogLevel.Error, x.ToString());
				}
			}

			var sourceReferences = args.RequestingAssembly.GetReferencedAssemblies();
			var name2 = sourceReferences.FirstOrDefault(x => x.Name == args.Name);
			if (name2 != null)
			{
				Assembly loaded = Assembly.Load(name2);
				_knownAssemblies.AddOrUpdate(args.Name, loaded, (s, assembly1) => loaded);
				return loaded;
			}

			string dllOnly = args.Name;
			int idx = dllOnly.IndexOf(',', StringComparison.OrdinalIgnoreCase);
			if (idx >= 0)
			{
				dllOnly = dllOnly.Substring(0, idx);
				dllOnly += ".dll";
			}

			string currentDirectory = Path.GetDirectoryName(args.RequestingAssembly.Location) ?? string.Empty;
			string path = Path.Combine(currentDirectory, dllOnly);

			if (!File.Exists(path))
			{
				return null;
			}

			// If we find a file with the same name in the "runtimes" subfolder, we should probably pick that one instead of the one in the main directory.
			string osName = string.Empty;
			if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
			{
				osName = "win";
			}
			else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
			{
				osName = "unix";
			}

			string startDirectory = Path.Combine(currentDirectory, "runtimes", osName);
			if (Directory.Exists(startDirectory))
			{
				var potentialEntries = Directory.EnumerateFileSystemEntries(startDirectory, dllOnly, SearchOption.AllDirectories).ToArray();
				if (potentialEntries.Length > 0)
				{
					GetBestRuntimeDll(potentialEntries, ref path);
				}
			}

			var assembly = Assembly.LoadFile(path);
			_knownAssemblies.AddOrUpdate(args.Name, assembly, (s, assembly1) => assembly);
			return assembly;
		}

		internal static void GetBestRuntimeDll(string[] potentialEntries, ref string path)
		{
			if (potentialEntries.Length == 1)
			{
				path = potentialEntries[0];
			}
			else if (potentialEntries.Length > 1)
			{
				// Multiple files with this name exist in runtimes. Take the newest.
				float bestVersion = -1;
				int bestIndex = 0;
				for (var index = 0; index < potentialEntries.Length; index++)
				{
					var entry = potentialEntries[index];
					var matches = Regex.Matches(entry, RuntimeVersionRegex);
					if (matches.Any())
					{
						string version = matches[0].Value;
						if (float.TryParse(version, NumberStyles.Any, CultureInfo.InvariantCulture, out float thisVersion) && thisVersion > bestVersion)
						{
							bestIndex = index;
							bestVersion = thisVersion;
						}
					}
				}

				// This will in either case return an entry, let's hope it's the right one if the above fails
				path = potentialEntries[bestIndex];
			}
		}

		public void StartListening()
		{
			_listener = new TcpListener(IPAddress.Any, _networkPort);
			_threadRunning = true;
			_listener.Start();
			_mainThread = new Thread(ReceiverThread);
			_mainThread.Start();
		}

		/// <summary>
		/// Starts the server stream handler directly
		/// </summary>
		internal void StartProcessing(string otherSideInstanceId)
		{
			if (_preopenedStream == null)
			{
				throw new InvalidOperationException($"{nameof(StartProcessing)} must only be called for the client's own server");
			}

			_threadRunning = true;
			Thread ts = new Thread(ServerStreamHandler);
			ts.Name = "Server stream handler for client side";
			var td = new ThreadData(ts, _preopenedStream, new BinaryReader(_preopenedStream, Encoding.Unicode), otherSideInstanceId);
			_threads.Add(td);
			ts.Start(td);
		}

		private void ServerStreamHandler(object? obj)
		{
			ThreadData td = obj as ThreadData ?? throw new ArgumentNullException(nameof(obj));
			var stream = td.Stream;
			var r = td.BinaryReader;

			List<Task> openTasks = new List<Task>();

			try
			{
				while (_threadRunning)
				{
					// Clean up task list
					for (var index = 0; index < openTasks.Count; index++)
					{
						var task = openTasks[index];
						if (task.Exception != null)
						{
							throw new RemotingException("Unhandled task exception in remote server");
						}

						if (task.IsCompleted)
						{
							openTasks.Remove(task);
							index--;
						}
					}

					RemotingCallHeader hd = default;
					if (!hd.ReadFrom(r))
					{
						throw new RemotingException("Incorrect data stream - sync lost");
					}

					if (ExecuteCommand(hd, r, td.OtherSideInstanceId, out bool clientDisconnecting))
					{
						if (clientDisconnecting)
						{
							Logger.Log(LogLevel.Error, $"Server handler disconnecting upon request.");
							// Just close the stream and return. Do NOT throw an exception.
							stream.Dispose();
							return;
						}

						continue;
					}

					string instance = r.ReadString();
					string typeOfCallerName = r.ReadString();
					Type? typeOfCaller = null;
					if (!string.IsNullOrEmpty(typeOfCallerName))
					{
						typeOfCaller = GetTypeFromAnyAssembly(typeOfCallerName);
					}

					int methodNo = r.ReadInt32(); // token of method to call
					int methodGenericArgs = r.ReadInt32(); // number of generic arguments of method (not generic arguments of declaring class!)

					MemoryStream ms = new MemoryStream(4096);
					BinaryWriter answerWriter = new BinaryWriter(ms, Encoding.Unicode);

					if (hd.Function == RemotingFunctionType.CreateInstanceWithDefaultCtor)
					{
						// CreateInstance call, instance is just a type in this case
						if (methodGenericArgs != 0)
						{
							throw new RemotingException("Constructors cannot have generic arguments");
						}

						Type? t;
						if (!TryReflectionMethod(() => GetTypeFromAnyAssembly(instance), answerWriter, hd.Sequence, td.OtherSideInstanceId, out t) || t == null)
						{
							SendAnswer(td, ms);
							continue;
						}

						object? newInstance = null;
						if (!TryReflectionMethod(() => Activator.CreateInstance(t, false), answerWriter, hd.Sequence, td.OtherSideInstanceId, out newInstance))
						{
							continue;
						}

						RemotingCallHeader hdReturnValue1 = new RemotingCallHeader(RemotingFunctionType.MethodReply, hd.Sequence);
						hdReturnValue1.WriteTo(answerWriter);
						// Can this fail? Typically, this is a reference type, so it shouldn't.
						_messageHandler.WriteArgumentToStream(answerWriter, newInstance, td.OtherSideInstanceId);

						SendAnswer(td, ms);

						continue;
					}

					if (hd.Function == RemotingFunctionType.CreateInstance)
					{
						// CreateInstance call, instance is just a type in this case
						if (methodGenericArgs != 0)
						{
							throw new RemotingException("Constructors cannot have generic arguments");
						}

						int numArguments = r.ReadInt32();
						object?[] ctorArgs = new object[numArguments];
						for (int i = 0; i < ctorArgs.Length; i++)
						{
							// Constructors are selected dynamically on the server side (below), therefore we can't pass the argument type here.
							// This may disallow calling a constructor with a client-side reference. It is yet to clarify whether that's a problem or not.
							ctorArgs[i] = _messageHandler.ReadArgumentFromStream(r, null, null, false, null, td.OtherSideInstanceId);
						}

						Type? t;
						if (!TryReflectionMethod(() => GetTypeFromAnyAssembly(instance), answerWriter, hd.Sequence, td.OtherSideInstanceId, out t) || t == null)
						{
							SendAnswer(td, ms);
							continue;
						}

						object? newInstance;
						if (!TryReflectionMethod(() => Activator.CreateInstance(t, ctorArgs), answerWriter, hd.Sequence, td.OtherSideInstanceId, out newInstance))
						{
							SendAnswer(td, ms);
							continue;
						}

						RemotingCallHeader hdReturnValue1 = new RemotingCallHeader(RemotingFunctionType.MethodReply, hd.Sequence);
						hdReturnValue1.WriteTo(answerWriter);
						_messageHandler.WriteArgumentToStream(answerWriter, newInstance, td.OtherSideInstanceId);

						SendAnswer(td, ms);
						continue;
					}

					if (hd.Function == RemotingFunctionType.RequestServiceReference)
					{
						Type? t = null;
						if (!TryReflectionMethod(() => GetTypeFromAnyAssembly(instance), answerWriter, hd.Sequence, td.OtherSideInstanceId, out t))
						{
							SendAnswer(td, ms);
							continue;
						}

						object? newInstance = null;
						if (t != null)
						{
							newInstance = ServiceContainer.GetService(t);
						}

						RemotingCallHeader hdReturnValue1 = new RemotingCallHeader(RemotingFunctionType.MethodReply, hd.Sequence);
						hdReturnValue1.WriteTo(answerWriter);
						_messageHandler.WriteArgumentToStream(answerWriter, newInstance, td.OtherSideInstanceId);

						SendAnswer(td, ms);
						continue;
					}

					List<Type> typeOfGenericArguments = new List<Type>();
					for (int i = 0; i < methodGenericArgs; i++)
					{
						var typeName = r.ReadString();
						var t = GetTypeFromAnyAssembly(typeName);
						typeOfGenericArguments.Add(t);
					}

					object realInstance = _instanceManager.GetObjectFromId(instance);

					if (typeOfCaller == null)
					{
						typeOfCaller = realInstance.GetType();
					}

					var methods = typeOfCaller.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
					MethodInfo me = methods.First(x => x.MetadataToken == methodNo);

					if (me == null)
					{
						throw new RemotingException($"No such method: {methodNo}");
					}

					if (methodGenericArgs > 0)
					{
						me = me.MakeGenericMethod(typeOfGenericArguments.ToArray());
					}

					int numArgs = r.ReadInt32();
					object?[] args = new object[numArgs];
					for (int i = 0; i < numArgs; i++)
					{
						var decodedArg = _messageHandler.ReadArgumentFromStream(r, me, null, false, me.GetParameters()[i].ParameterType, td.OtherSideInstanceId);
						args[i] = decodedArg;
					}

					openTasks.Add(Task.Run(() =>
					{
						try
						{
							InvokeRealInstance(me, realInstance, args, hd, answerWriter, td.OtherSideInstanceId);
						}
						catch (SerializationException x)
						{
							// The above throws if serializing the return value(s) fails (return type of method call not serializable)
							// Clear the stream, we must only send the exception
							ms.Position = 0;
							ms.SetLength(0);
							_messageHandler.SendExceptionReply(x, answerWriter, hd.Sequence, td.OtherSideInstanceId);
						}

						SendAnswer(td, ms);
					}));
				}

				Logger.Log(LogLevel.Information, $"Server on port {NetworkPort} exited");
			}
			catch (Exception x) when (x is IOException || x is ObjectDisposedException)
			{
				// Remote connection closed
				Logger.Log(LogLevel.Error, $"Server handler died due to {x}");
				if (KillProcessWhenChannelDisconnected)
				{
					_terminatingSource.Cancel();
				}
			}
		}

		private void SendAnswer(ThreadData thread, MemoryStream rawDataMessage)
		{
			lock (thread)
			{
				rawDataMessage.Position = 0;
				try
				{
					rawDataMessage.CopyTo(thread.Stream);
				}
				catch (ObjectDisposedException x)
				{
					// Ignore
					Logger.LogWarning(x, $"It appears the connection is closed: {x.Message}");
				}

				rawDataMessage.Dispose();
			}
		}

		/// <summary>
		/// Calls the given operator, returning its result.
		/// If the function throws an exception related to reflection (TypeLoadException or similar), the exception is sent back over the stream
		/// </summary>
		/// <typeparam name="T">The return type of the operation</typeparam>
		/// <param name="operation">Whatever needs to be done</param>
		/// <param name="writer">Binary writer to send the exception back</param>
		/// <param name="sequenceId">Sequence Id of the call</param>
		/// <param name="result">[Out] Result of the operation</param>
		/// <returns>True on success, false if an exception occurred</returns>
		private bool TryReflectionMethod<T>(Func<T> operation, BinaryWriter writer, int sequenceId, string otherSideInstanceId, out T? result)
		{
			T? ret = default;
			try
			{
				ret = operation();
			}
			catch (Exception x) when (x is TypeLoadException || x is FileNotFoundException || x is TargetInvocationException)
			{
				_messageHandler.SendExceptionReply(x, writer, sequenceId, otherSideInstanceId);
				result = default;
				return false;
			}

			result = ret;
			return true;
		}

		private void InvokeRealInstance(MethodInfo me, object realInstance, object?[] args, RemotingCallHeader hd, BinaryWriter w, string otherSideInstanceId)
		{
			// Here, the actual target method of the proxied call is invoked.
			Logger.Log(LogLevel.Debug, $"MainServer: Invoking {me}, sequence {hd.Sequence}");
			object? returnValue;
			try
			{
				if (realInstance == null && !me.IsStatic)
				{
					throw new NullReferenceException("Cannot invoke on a non-static method without an instance");
				}

				if (realInstance is Delegate del)
				{
					returnValue = me.Invoke(del.Target, args);
				}
				else
				{
					returnValue = me.Invoke(realInstance, args);
				}
			}
			catch (Exception x)
			{
				Logger.Log(LogLevel.Debug, $"Invoking threw {x}");
				lock (_channelWriterLock)
				{
					if (x.InnerException != null)
					{
						// the exception we want to forward is here the inner exception
						x = x.InnerException;
					}

					_messageHandler.SendExceptionReply(x, w, hd.Sequence, otherSideInstanceId);
				}

				return;
			}

			// Ensure only one thread writes data to the stream at a time (to keep the data from one message together)
			lock (_channelWriterLock)
			{
				RemotingCallHeader hdReturnValue = new RemotingCallHeader(RemotingFunctionType.MethodReply, hd.Sequence);
				hdReturnValue.WriteTo(w);
				if (me.ReturnType != typeof(void))
				{
					if (returnValue == null)
					{
						Logger.Log(LogLevel.Debug, $"MainServer: {hd.Sequence} reply is null");
					}
					else
					{
						Logger.Log(LogLevel.Debug, $"MainServer: {hd.Sequence} reply is of type {returnValue.GetType()}");
					}

					_messageHandler.WriteArgumentToStream(w, returnValue, otherSideInstanceId);
				}

				int index = 0;
				foreach (var byRefArguments in me.GetParameters())
				{
					if (byRefArguments.ParameterType.IsByRef)
					{
						_messageHandler.WriteArgumentToStream(w, args[index], otherSideInstanceId);
					}

					index++;
				}
			}
		}

		private bool ExecuteCommand(RemotingCallHeader hd, BinaryReader r, string otherSideInstanceId, out bool clientDisconnecting)
		{
			clientDisconnecting = false;
			if (hd.Function == RemotingFunctionType.OpenReverseChannel)
			{
				string clientIp = r.ReadString();
				int clientPort = r.ReadInt32();
				string otherSideInstanceId1 = r.ReadString();
				int connectionIdentifier = r.ReadInt32();
				Stream? streamToUse;
				while (!_clientStreamsExpectingUse.TryGetValue(connectionIdentifier, out streamToUse))
				{
					// Wait until the matching connection is available - when several clients connect simultaneously, they may interfere here
					Thread.Sleep(20);
					// throw new RemotingException("Server not ready for reverse connection. Startup sequence error", RemotingExceptionKind.ProtocolError);
				}

				var newInterceptor = new ClientSideInterceptor(otherSideInstanceId1, _instanceManager.InstanceIdentifier, false, streamToUse, _messageHandler, Logger);

				newInterceptor.Start();
				_messageHandler.AddInterceptor(newInterceptor);
				_instanceManager.AddInterceptor(newInterceptor);
				_serverInterceptorForCallbacks.Add(otherSideInstanceId1, newInterceptor);
				return true;
			}

			if (hd.Function == RemotingFunctionType.ClientDisconnecting)
			{
				string clientName = r.ReadString();
				if (_serverInterceptorForCallbacks.TryGetValue(clientName, out var interceptor))
				{
					interceptor.Dispose();
				}

				clientDisconnecting = true;
				return true;
			}

			if (hd.Function == RemotingFunctionType.LoadClientAssemblyIntoServer)
			{
				string assemblyName = r.ReadString();
				AssemblyName name = new AssemblyName(assemblyName);
				try
				{
					var assembly = Assembly.Load(name);
					_knownAssemblies.TryAdd(name.FullName, assembly);
				}
				catch (Exception x)
				{
					Logger.Log(LogLevel.Error, x.ToString());
				}

				return true;
			}

			if (hd.Function == RemotingFunctionType.GcCleanup)
			{
				int cnt = r.ReadInt32(); // Number of elements that follow
				for (int i = 0; i < cnt; i++)
				{
					string objectId = r.ReadString();
					_instanceManager.Remove(objectId, otherSideInstanceId);
				}

				return true;
			}

			if (hd.Function == RemotingFunctionType.ShutdownServer)
			{
				_terminatingSource.Cancel();
				return true;
			}

			return false;
		}

		public static Type GetTypeFromAnyAssembly(string assemblyQualifiedName)
		{
			Type? t = Type.GetType(assemblyQualifiedName);
			if (t != null)
			{
				return t;
			}

			int start = 0;
			if (assemblyQualifiedName.Contains("]"))
			{
				// If the type contains a generic argument, its type is embedded in square brackets. We want the assembly of the final type, though
				start = assemblyQualifiedName.LastIndexOf("]", StringComparison.OrdinalIgnoreCase);
			}

			int idx = assemblyQualifiedName.IndexOf(',', start);
			if (idx > 0)
			{
				string assemblyName = assemblyQualifiedName.Substring(idx + 2);
				AssemblyName name = new AssemblyName(assemblyName);

				string typeName = assemblyQualifiedName.Substring(0, idx);

				try
				{
					Assembly ass = Assembly.Load(name);
					try
					{
						// When the second argument is true, this never returns null
						return ass.GetType(assemblyQualifiedName, true)!;
					}
					catch (ArgumentException)
					{
						return ass.GetType(typeName, true)!;
					}
				}
				catch (FileNotFoundException)
				{
					// Try the legacy way
					Assembly ass = Assembly.LoadFrom(name.Name + ".dll");
					return ass.GetType(typeName, true)!;
				}
			}

			throw new TypeLoadException($"Type {assemblyQualifiedName} not found. No assembly specified");
		}

		/// <summary>
		/// This controls the master Socket.Accept thread.
		/// </summary>
		private void ReceiverThread()
		{
			while (_threadRunning)
			{
				try
				{
					if (_listener == null)
					{
						return;
					}

					var tcpClient = _listener.AcceptTcpClient();
					var stream = tcpClient.GetStream();
					byte[] authenticationToken = new byte[100];

					// TODO: This needs some kind of authentication / trust management, but I have _no_ clue on how to get that safely done,
					// since we (by design) want anyone to be able to connect and execute arbitrary code locally. Bad combination...
					if (stream.Read(authenticationToken, 0, 100) != 100)
					{
						tcpClient.Dispose();
						continue;
					}

					byte[] length = new byte[4];
					if (stream.Read(length, 0, 4) != 4)
					{
						tcpClient.Dispose();
						continue;
					}

					byte[] data = new byte[BitConverter.ToInt32(length)];

					if (stream.Read(data, 0, data.Length) != data.Length)
					{
						tcpClient.Dispose();
						continue;
					}

					string otherSideInstanceId = Encoding.Unicode.GetString(data);

					int instanceHash = BitConverter.ToInt32(authenticationToken, 1);

					SendAuthenticationReply(stream);

					if (authenticationToken[0] == 1)
					{
						_clientStreamsExpectingUse.TryAdd(instanceHash, tcpClient.GetStream());
						continue;
					}

					Thread ts = new Thread(ServerStreamHandler);
					ts.Name = $"Remote server client {tcpClient.Client.RemoteEndPoint}";
					var td = new ThreadData(ts, stream, new BinaryReader(stream, Encoding.Unicode), otherSideInstanceId);
					_threads.Add(td);
					ts.Start(td);
				}
				catch (SocketException x)
				{
					Console.WriteLine($"Server terminating? Got {x}");
				}
			}
		}

		private void SendAuthenticationReply(Stream stream)
		{
			byte[] successToken = BitConverter.GetBytes(AuthenticationSucceededToken);
			stream.Write(successToken);

			var instanceIdentifier = Encoding.Unicode.GetBytes(_instanceManager.InstanceIdentifier);
			var lenBytes = BitConverter.GetBytes(instanceIdentifier.Length);
			stream.Write(lenBytes);
			stream.Write(instanceIdentifier);
		}

		/// <summary>
		/// Terminate a link
		/// </summary>
		/// <param name="disconnected">True if the client has already logically disconnected</param>
		public void Terminate(bool disconnected)
		{
			if (!disconnected)
			{
				MemoryStream ms = new MemoryStream();
				BinaryWriter binaryWriter = new BinaryWriter(ms, Encoding.Unicode);
				RemotingCallHeader hdReturnValue = new RemotingCallHeader(RemotingFunctionType.ServerShuttingDown, 0);
				hdReturnValue.WriteTo(binaryWriter);
				foreach (var thread in _threads)
				{
					try
					{
						SendAnswer(thread, ms);
					}
					catch (Exception x) when (x is IOException || x is ObjectDisposedException)
					{
						Logger.LogInformation(x, $"Sending termination command to client {thread.Thread.Name} failed. Ignoring.");
					}
				}
			}

			_preopenedStream?.Dispose();
			_preopenedStream = null;

			_threadRunning = false;
			foreach (var thread in _threads)
			{
				thread.Stream.Close();
				thread.BinaryReader.Dispose();
				thread.Thread.Join();
			}

			_threads.Clear();

			foreach (var clientThread in _serverInterceptorForCallbacks)
			{
				clientThread.Value.Dispose();
			}

			_serverInterceptorForCallbacks.Clear();

			_listener?.Stop();
			_mainThread?.Join();
		}

		public void Dispose()
		{
			Terminate(false);
		}

		public void WaitForTermination()
		{
			_terminatingSource.Token.WaitHandle.WaitOne();
		}

		private sealed class ThreadData
		{
			public ThreadData(Thread thread, Stream stream, BinaryReader binaryReader, string otherSideInstanceId)
			{
				Thread = thread;
				Stream = stream;
				BinaryReader = binaryReader;
				OtherSideInstanceId = otherSideInstanceId;
			}

			public Thread Thread { get; }
			public Stream Stream { get; }
			public BinaryReader BinaryReader { get; }

			public string OtherSideInstanceId { get; }
		}
	}
}
