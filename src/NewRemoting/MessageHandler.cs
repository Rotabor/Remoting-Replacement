﻿using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Dynamic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using System.Threading.Tasks;
using Castle.DynamicProxy;

namespace NewRemoting
{
	internal class MessageHandler
	{
		private readonly InstanceManager _instanceManager;
		private readonly IFormatter _formatter;
		private readonly object _communicationLock;
		private readonly Dictionary<string, ClientSideInterceptor> _interceptors;
		private bool _initialized;

		public MessageHandler(InstanceManager instanceManager, IFormatter formatter)
		{
			_instanceManager = instanceManager;
			_formatter = formatter;
			_communicationLock = new object();
			_initialized = false;
			_interceptors = new();
		}

		/// <summary>
		/// Exposing this is a bit dangerous, but since everything is internal, it should be fine
		/// </summary>
		internal object CommunicationLinkLock => _communicationLock;

		public InstanceManager InstanceManager => _instanceManager;

		public static bool HasDefaultCtor(Type t)
		{
			var ctor = t.GetConstructor(BindingFlags.Public | BindingFlags.Instance, null, CallingConventions.Any, new Type[0], null);
			return ctor != null;
		}

		public void WriteArgumentToStream(BinaryWriter w, object data)
		{
			if (!_initialized)
			{
				throw new InvalidOperationException("Instance is not initialized");
			}

			if (ReferenceEquals(data, null))
			{
				w.Write((int)RemotingReferenceType.NullPointer);
				return;
			}

			Type t = data.GetType();
			if (data is Type type)
			{
				w.Write((int)RemotingReferenceType.InstanceOfSystemType);
				w.Write(type.AssemblyQualifiedName);
			}

			// IPAddress is not serializable, even though it is actually trivially-serializable
			else if (data is IPAddress address)
			{
				w.Write((int)RemotingReferenceType.IpAddress);
				string s = address.ToString();
				w.Write(s);
			}
			else if (data is Type[] typeArray)
			{
				w.Write((int)RemotingReferenceType.ArrayOfSystemType);
				w.Write(typeArray.Length);
				for (int i = 0; i < typeArray.Length; i++)
				{
					if (typeArray[i] == null)
					{
						w.Write(String.Empty);
					}
					else
					{
						w.Write(typeArray[i].AssemblyQualifiedName);
					}
				}
			}
			else if (TypeIsContainerWithReference(data, out Type contentType))
			{
				var list = data as IEnumerable;
				w.Write((int)RemotingReferenceType.ContainerType);
				w.Write(data.GetType().AssemblyQualifiedName);
				w.Write(contentType.AssemblyQualifiedName);
				foreach (object obj in list)
				{
					// Recursively write the arguments
					w.Write(true);
					WriteArgumentToStream(w, obj);
				}

				w.Write(false); // Terminate the array
			}
			else if (data is Delegate del)
			{
				if (del.Method.IsStatic)
				{
					throw new RemotingException("Can only register instance methods as delegate targets", RemotingExceptionKind.UnsupportedOperation);
				}

				// The argument is a function pointer (typically the argument to a add_ or remove_ event)
				w.Write((int)RemotingReferenceType.MethodPointer);
				if (del.Target != null)
				{
					string instanceId = _instanceManager.GetIdForObject(del.Target);
					w.Write(instanceId);
				}
				else
				{
					// The delegate target is a static method
					w.Write(string.Empty);
				}

				string targetId = _instanceManager.GetIdForObject(del);
				w.Write(targetId);
				w.Write(del.Method.DeclaringType.AssemblyQualifiedName);
				w.Write(del.Method.MetadataToken);
			}
			else if (Client.IsRemoteProxy(data))
			{
				// Proxies are never serializable
				if (!_instanceManager.TryGetObjectId(data, out string objectId))
				{
					throw new RemotingException("A proxy has no existing reference", RemotingExceptionKind.ProxyManagementError);
				}

				w.Write((int)RemotingReferenceType.RemoteReference);
				w.Write(objectId);
				w.Write(string.Empty);
			}
			else if (t.IsSerializable)
			{
				SendSerializedObject(w, data);
			}
			else if (t.IsAssignableTo(typeof(MarshalByRefObject)))
			{
				string objectId = _instanceManager.GetIdForObject(data);
				w.Write((int)RemotingReferenceType.RemoteReference);
				w.Write(objectId);
				w.Write(data.GetType().AssemblyQualifiedName);
			}
			else
			{
				throw new SerializationException($"Object {data} is neither serializable nor MarshalByRefObject");
			}
		}

		private void SendSerializedObject(BinaryWriter w, object data)
		{
			MemoryStream ms = new MemoryStream();

#pragma warning disable 618
			_formatter.Serialize(ms, data);
#pragma warning restore 618
			w.Write((int)RemotingReferenceType.SerializedItem);
			w.Write((int)ms.Length);
			// The following is for testing purposes only (slow!)
			var array = ms.ToArray();
			byte[] compare = Encoding.ASCII.GetBytes("DynamicProxyGenAss");
			for (int i = 0; i < array.Length; i++)
			{
				int needleIdx = 0;
				while (needleIdx < compare.Length && array[i + needleIdx] == compare[needleIdx])
				{
					needleIdx++;
				}

				if (needleIdx >= compare.Length)
				{
					ms.Position = 0;
#pragma warning disable 618
					_formatter.Serialize(ms, data);
#pragma warning restore 618
					throw new RemotingException("Should not have serialized a dynamic proxy with its internal name", RemotingExceptionKind.UnsupportedOperation);
				}
			}

			w.Write(array, 0, (int)ms.Length);
		}

		/// <summary>
		/// True when this type implements <see cref="IList{T}" /> with T being <see cref="MarshalByRefObject"/>.
		/// </summary>
		private bool TypeIsContainerWithReference(object data, out Type type)
		{
			if (data is IList enumerable)
			{
				var args = enumerable.GetType().GenericTypeArguments;
				if (args.Length == 1)
				{
					type = args[0];
					if (type.IsSubclassOf(typeof(MarshalByRefObject)))
					{
						return true;
					}

					if (type.IsSerializable)
					{
						return false;
					}

					// Otherwise, we have to test the contents.
					return ContentIsMarshalByRef(enumerable);
				}
				else if (args.Length > 1)
				{
					// Not currently supported
					type = null;
					return false;
				}
				else
				{
					// Data implements IEnumerable, but not IEnumerable{T}.
					type = typeof(object);
					return ContentIsMarshalByRef(enumerable);
				}
			}

			type = null;
			return false;
		}

		private bool ContentIsMarshalByRef(IList enumerable)
		{
			foreach (var e in enumerable)
			{
				if (e == null)
				{
					continue;
				}

				Type t = e.GetType();
				if (t.IsSubclassOf(typeof(MarshalByRefObject)) || ProxyUtil.IsProxyType(t))
				{
					return true;
				}
			}

			return false;
		}

		public void ProcessCallResponse(IInvocation invocation, BinaryReader reader)
		{
			if (!_initialized)
			{
				throw new InvalidOperationException("Instance is not initialized");
			}

			MethodBase methodBase;
			// This is true if this is a reply to a CreateInstance call (invocation.Method cannot be a ConstructorInfo instance)
			if (invocation is ManualInvocation mi && mi.Method == null && mi.Constructor != null)
			{
				methodBase = mi.Constructor;

				object returnValue = ReadArgumentFromStream(reader, methodBase, invocation, true, methodBase.DeclaringType);
				invocation.ReturnValue = returnValue;
				// out or ref arguments on ctors are rare, but not generally forbidden, so we continue here
			}
			else if (invocation is ManualInvocation mi2 && mi2.TargetType != null)
			{
				// This happens if we request a remote instance directly (by interface type)
				object returnValue = ReadArgumentFromStream(reader, mi2.Method, invocation, true, mi2.TargetType);
				invocation.ReturnValue = returnValue;
				return;
			}
			else
			{
				MethodInfo me = invocation.Method;
				methodBase = me;
				if (me.ReturnType != typeof(void))
				{
					object returnValue = ReadArgumentFromStream(reader, methodBase, invocation, true, me.ReturnType);
					invocation.ReturnValue = returnValue;
				}
			}

			int index = 0;
			foreach (var byRefArguments in methodBase.GetParameters())
			{
				if (byRefArguments.ParameterType.IsByRef)
				{
					object byRefValue = ReadArgumentFromStream(reader, methodBase, invocation, false, byRefArguments.ParameterType);
					invocation.Arguments[index] = byRefValue;
				}

				index++;
			}
		}

		public void SendExceptionReply(Exception exception, BinaryWriter w, int sequence)
		{
			RemotingCallHeader hdReturnValue = new RemotingCallHeader(RemotingFunctionType.ExceptionReturn, sequence);
			hdReturnValue.WriteTo(w);
			SendSerializedObject(w, exception);
		}

		public object ReadArgumentFromStream(BinaryReader r, MethodBase callingMethod, IInvocation invocation, bool canAttemptToInstantiate, Type typeOfArgument)
		{
			if (!_initialized)
			{
				throw new InvalidOperationException("Instance is not initialized");
			}

			RemotingReferenceType referenceType = (RemotingReferenceType)r.ReadInt32();
			switch (referenceType)
			{
				case RemotingReferenceType.NullPointer:
					return null;
				case RemotingReferenceType.SerializedItem:
				{
					int argumentLen = r.ReadInt32();
					byte[] argumentData = r.ReadBytes(argumentLen);
					MemoryStream ms = new MemoryStream(argumentData, false);
#pragma warning disable 618
					object decodedArg = _formatter.Deserialize(ms);
#pragma warning restore 618
					return decodedArg;
				}

				case RemotingReferenceType.RemoteReference:
				{
					// The server sends a reference to an object that he owns
					string objectId = r.ReadString();
					string typeName = r.ReadString();
					object instance = null;
					instance = InstanceManager.CreateOrGetReferenceInstance(invocation, canAttemptToInstantiate, typeOfArgument, typeName, objectId);
					return instance;
				}

				case RemotingReferenceType.InstanceOfSystemType:
				{
					string typeName = r.ReadString();
					Type t = Server.GetTypeFromAnyAssembly(typeName);
					return t;
				}

				case RemotingReferenceType.ArrayOfSystemType:
				{
					int count = r.ReadInt32();
					Type[] ts = new Type[count];
					for (int i = 0; i < count; i++)
					{
						string typeName = r.ReadString();
						if (!string.IsNullOrEmpty(typeName))
						{
							Type t = Server.GetTypeFromAnyAssembly(typeName);
							ts[i] = t;
						}
						else
						{
							ts[i] = null;
						}
					}

					return ts;
				}

				case RemotingReferenceType.ContainerType:
				{
					string typeName = r.ReadString();
					Type t = Server.GetTypeFromAnyAssembly(typeName);
					Type contentType = Server.GetTypeFromAnyAssembly(r.ReadString());
					IList list = (IList)Activator.CreateInstance(t);
					bool cont = r.ReadBoolean();
					while (cont)
					{
						var nextElem = ReadArgumentFromStream(r, callingMethod, invocation, canAttemptToInstantiate, contentType);
						list.Add(nextElem);
						cont = r.ReadBoolean();
					}

					return list;
				}

				case RemotingReferenceType.IpAddress:
				{
					string s = r.ReadString();
					return IPAddress.Parse(s);
				}

				case RemotingReferenceType.MethodPointer:
				{
					string instanceId = r.ReadString();
					string targetId = r.ReadString();
					string typeOfTargetName = r.ReadString();
					int tokenOfTargetMethod = r.ReadInt32();
					Type typeOfTarget = Server.GetTypeFromAnyAssembly(typeOfTargetName);

					var methods = typeOfTarget.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
					MethodInfo methodInfoOfTarget = methods.First(x => x.MetadataToken == tokenOfTargetMethod);

					// TODO: This copying of arrays here is not really performance-friendly
					var argumentsOfTarget = methodInfoOfTarget.GetParameters().Select(x => x.ParameterType).ToList();

					var interceptor = InstanceManager.GetInterceptor(_interceptors, instanceId);
					// This creates an instance of the DelegateInternalSink class, which acts as a proxy for delegate callbacks. Instead of the actual delegate
					// target, we register a method from this class as a delegate target
					var internalSink = new DelegateInternalSink(interceptor, targetId, methodInfoOfTarget);
					_instanceManager.AddInstance(internalSink, targetId);

					IEnumerable<MethodInfo> possibleSinks = null;

					MethodInfo localSinkTarget;
					if (methodInfoOfTarget.ReturnType == typeof(void))
					{
						possibleSinks = internalSink.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public).Where(x => x.Name == "ActionSink");
						localSinkTarget = possibleSinks.Single(x => x.GetGenericArguments().Length == argumentsOfTarget.Count);
					}
					else
					{
						possibleSinks = internalSink.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public).Where(x => x.Name == "FuncSink");
						localSinkTarget = possibleSinks.Single(x => x.GetGenericArguments().Length == argumentsOfTarget.Count + 1);
						argumentsOfTarget.Add(methodInfoOfTarget.ReturnType);
					}

					if (argumentsOfTarget.Count > 0)
					{
						localSinkTarget = localSinkTarget.MakeGenericMethod(argumentsOfTarget.ToArray());
					}

					string delegateId = instanceId + "." + methodInfoOfTarget.Name;
					Delegate newDelegate;
					if (callingMethod != null && callingMethod.IsSpecialName && callingMethod.Name.StartsWith("add_", StringComparison.Ordinal))
					{
						newDelegate = Delegate.CreateDelegate(typeOfArgument, internalSink, localSinkTarget);
						_instanceManager.AddInstance(newDelegate, delegateId);
						return newDelegate;
					}
					else if (callingMethod != null && callingMethod.IsSpecialName && callingMethod.Name.StartsWith("remove_", StringComparison.Ordinal))
					{
						// This may fail if the delegate was already removed. In this case, we just return a new instance, it will be GCed soon after
						if (_instanceManager.TryGetObjectFromId(delegateId, out var obj))
						{
							// Remove delegate (and forget about it), it is not used here any more.
							_instanceManager.Remove(delegateId);
							return obj;
						}
					}

					// No need to register - this is a delegate used as method argument in an "ordinary" call
					newDelegate = Delegate.CreateDelegate(typeOfArgument, internalSink, localSinkTarget);
					return newDelegate;
				}

				default:
					throw new RemotingException("Unknown argument type", RemotingExceptionKind.UnsupportedOperation);
			}
		}

		public Exception DecodeException(BinaryReader reader)
		{
			reader.ReadInt32(); // RemotingReferenceType.SerializedItem
			int argumentLen = reader.ReadInt32();
			byte[] argumentData = reader.ReadBytes(argumentLen);
			MemoryStream ms = new MemoryStream(argumentData, false);
#pragma warning disable 618
			object decodedArg = _formatter.Deserialize(ms);
#pragma warning restore 618
			return (Exception)decodedArg;
		}

		public void AddInterceptor(ClientSideInterceptor newInterceptor)
		{
			_interceptors.Add(newInterceptor.OtherSideInstanceId, newInterceptor);
			_initialized = true; // at least one entry exists
		}
	}
}