﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using NewRemoting;
using NUnit.Framework;
using SampleServerClasses;

namespace NewRemotingUnitTest
{
	[TestFixture(true)]
	[TestFixture(false)]
	[NonParallelizable]
	public class RemoteOperationsTest
	{
		private Process _serverProcess;
		private Client _client;
		private string _dataReceived;
		private string _dataReceived2;
		private AuthenticationHelper _helper;

		public RemoteOperationsTest(bool withAuthentication)
		{
			if (withAuthentication)
			{
				_helper = new AuthenticationHelper();
			}
		}

		public AuthenticationInformation CreateClientServer()
		{
			FileInfo fi = new FileInfo(Assembly.GetExecutingAssembly().Location);
			var psi = new ProcessStartInfo(Path.Combine(fi.DirectoryName, "RemotingServer.exe"));

			Console.WriteLine($"Attempting to start {psi.FileName}...");
			Console.WriteLine($"Current directory is {Environment.CurrentDirectory}");
			AuthenticationInformation authenticationInfo = _helper == null ? null : new AuthenticationInformation(_helper.CertificateFileName, _helper.CertificatePassword);

			_serverProcess = Process.Start(psi);
			Assert.IsNotNull(_serverProcess);

			// Port is currently hardcoded
			_client = new Client("localhost", Client.DefaultNetworkPort, authenticationInfo, null, null);
			_client.Start();
			return authenticationInfo;
		}

		[OneTimeSetUp]
		public void OneTimeSetUp()
		{
			_helper?.SetUp();
		}

		[OneTimeTearDown]
		public void OneTimeTearDown()
		{
			_helper?.TearDown();

			GC.Collect();
			GC.WaitForPendingFinalizers();
			if (_client != null)
			{
				_client.ShutdownServer();
				_client.Dispose();
				_client = null;
			}

			if (_serverProcess != null)
			{
				Assert.That(_serverProcess.WaitForExit(5000));
				_serverProcess.Kill();
				_serverProcess = null;
			}
		}

		[Test]
		public void LocalObjectIsNotProxy()
		{
			var instance = new MarshallableClass();
			// There have been reports that this returns false positives
			Assert.False(Client.IsRemoteProxy(instance));
		}

		[Test]
		public void GetInitialRemoteInstance()
		{
			CreateClientServer();
			var instance = CreateRemoteInstance();
			Assert.IsNotNull(instance);
			Assert.That(Client.IsRemoteProxy(instance));
		}

		[Test]
		public void RemoteInstanceCanBeCalled()
		{
			CreateClientServer();
			var instance = CreateRemoteInstance();
			int remotePid = instance.GetCurrentProcessId();
			Assert.AreNotEqual(remotePid, Environment.ProcessId);
		}

		[Test]
		public void TwoRemoteInstancesAreNotEqual()
		{
			CreateClientServer();
			var instance1 = CreateRemoteInstance();
			var instance2 = CreateRemoteInstance();
			Assert.AreNotEqual(instance1.Name, instance2.Name);
		}

		[Test]
		public void SameInstanceIsUsedInTwoCalls()
		{
			CreateClientServer();
			var instance1 = CreateRemoteInstance();
			string a = instance1.Name;
			string b = instance1.Name;
			Assert.AreEqual(a, b);
		}

		[Test]
		public void CanCreateInstanceWithNonDefaultCtor()
		{
			CreateClientServer();
			var instance = _client.CreateRemoteInstance<MarshallableClass>("MyInstance");
			Assert.AreEqual("MyInstance", instance.Name);
		}

		[Test]
		public void CanMarshalSystemType()
		{
			CreateClientServer();
			var instance = CreateRemoteInstance();
			Assert.AreEqual("System.String", instance.GetTypeName(typeof(System.String)));
		}

		[Test]
		public void CanMarshalNullReference()
		{
			CreateClientServer();
			var instance = CreateRemoteInstance();
			instance.RegisterCallback(null);
		}

		[Test]
		public void CodeIsReallyExecutedRemotely()
		{
			CreateClientServer();
			var server = CreateRemoteInstance();
			var client = new MarshallableClass();
			Assert.AreNotEqual(server.GetCurrentProcessId(), client.GetCurrentProcessId());
		}

		[Test]
		public void RefArgumentWorks()
		{
			CreateClientServer();
			var server = CreateRemoteInstance();
			int aValue = 4;
			server.UpdateArgument(ref aValue);
			Assert.AreEqual(6, aValue);
		}

		[Test]
		public void CanRegisterCallbackInterface()
		{
			CreateClientServer();
			var server = CreateRemoteInstance();
			// Tests whether the return channel works, by providing an instance of a class to the server where
			// the actual object lives on the client.
			var cbi = new CallbackImpl();
			Assert.False(cbi.HasBeenCalled);
			server.RegisterCallback(cbi);
			server.DoCallback();
			server.RegisterCallback(null);
			Assert.True(cbi.HasBeenCalled);
		}

		[Test]
		public void DoesNotThrowExceptionIfAttemptingToRegisterPrivateMethodAsEventSink()
		{
			CreateClientServer();
			var server = CreateRemoteInstance();
			var objectWithEvent = server.GetInterface<IMyComponentInterface>();
			_dataReceived = null;
			Assert.DoesNotThrow(() => objectWithEvent.TimeChanged += ObjectWithEventOnTimeChangedPrivate);
		}

		[Test]
		public void CanRegisterEvent()
		{
			CreateClientServer();
			var server = CreateRemoteInstance();
			var objectWithEvent = server.GetInterface<IMyComponentInterface>();
			_dataReceived = null;
			objectWithEvent.TimeChanged += ObjectWithEventOnTimeChanged;
			objectWithEvent.StartTiming();
			int ticks = 100;
			while (string.IsNullOrEmpty(_dataReceived) && ticks-- > 0)
			{
				Thread.Sleep(100);
			}

			Assert.That(ticks > 0);
			Assert.False(string.IsNullOrWhiteSpace(_dataReceived));
			objectWithEvent.StopTiming();
			objectWithEvent.TimeChanged -= ObjectWithEventOnTimeChanged;
		}

		public void ObjectWithEventOnTimeChanged(DateTime arg1, string arg2)
		{
			_dataReceived = arg2;
		}

		private void ObjectWithEventOnTimeChangedPrivate(DateTime arg1, string arg2)
		{
			throw new NotSupportedException("This should never be called");
		}

		[Test]
		public void CreateRemoteInstanceWithNonDefaultCtor()
		{
			CreateClientServer();
			var arguments = new ConstructorArgument(new ReferencedComponent() { ComponentName = "ClientUnderTest" });
			var service = _client.CreateRemoteInstance<ServiceClass>(arguments);

			// This calls the server, who calls back into the client. So we get something that the client generated
			string roundTrippedAnswer = service.DoSomething();

			Assert.AreEqual("Wrapped by Server: ClientUnderTest", roundTrippedAnswer);
		}

		[Test]
		public void UseMixedInstanceAsArgument()
		{
			CreateClientServer();
			var server = CreateRemoteInstance();
			var reference = new ReferencedComponent() { Data = 10 };
			// This is a serializable class that has a MarshalByRef member
			SerializableClassWithMarshallableMembers sc = new SerializableClassWithMarshallableMembers(1, reference);

			int reply = server.UseMixedArgument(sc);

			Assert.AreEqual(10, reply);

			reply = sc.CallbackViaComponent();

			Assert.AreEqual(10, reply);

			reference.Data = 20;
			reply = sc.CallbackViaComponent();
			Assert.AreEqual(20, reply);

			var sc2 = sc.ReturnSelfToCaller();

			Assert.True(ReferenceEquals(sc, sc2));
		}

		/// <summary>
		/// This just verifies the test below
		/// </summary>
		[Test]
		public void UseSystemManagementLocally()
		{
			var bios = new CheckBiosVersion();

			string[] versions = bios.GetBiosVersions();
			Console.WriteLine($"Local bios versions are: {string.Join(", ", versions)}.");
		}

		[Test]
		public void ReflectionLoadSystemManagement()
		{
			// var name = new AssemblyName("System.Management");
			var name = new AssemblyName(
				"System.Management, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a");
			var assembly = Assembly.Load(name);
			Type t = assembly.GetType("System.Management.ManagementObjectSearcher", true);
			var instance = Activator.CreateInstance(t, "SELECT * FROM Win32_BIOS");

			Assert.IsNotNull(instance);
			((IDisposable)instance).Dispose();
		}

		[Test]
		public void UseRemoteSystemManagement()
		{
			CreateClientServer();
			var bios = _client.CreateRemoteInstance<CheckBiosVersion>();

			string[] versions = bios.GetBiosVersions();
			Console.WriteLine($"Server bios versions are: {string.Join(", ", versions)}.");
		}

		[Test]
		public void GetListOfMarshalByRefInstances()
		{
			CreateClientServer();
			var c = CreateRemoteInstance();
			var list = c.GetSomeComponents();
			Assert.That(list is List<ReferencedComponent>);
			Assert.AreEqual(2, list.Count);
			Assert.That(list[0].ComponentName == list[1].ComponentName);
		}

		[Test]
		public void HandleRemoteException()
		{
			CreateClientServer();
			var c = CreateRemoteInstance();
			bool didThrow = true;
			try
			{
				c.MaybeThrowException(0);
				didThrow = false;
			}
			catch (DivideByZeroException x)
			{
				Assert.IsNotNull(x);
				Assert.That(x.StackTrace.Contains("DoIntegerDivide")); // The private method on the remote side that actually throws
				Console.WriteLine(x);
			}

			Assert.That(didThrow);
		}

		[Test]
		public void GetRemotingServerService()
		{
			CreateClientServer();
			var serverService = _client.RequestRemoteInstance<IRemoteServerService>();
			Assert.That(serverService.Ping());
		}

		[Test]
		public void SerializeUnserializableArgument()
		{
			CreateClientServer();
			var cls = CreateRemoteInstance();
			Assert.Throws<SerializationException>(() => cls.CallerError(new UnserializableObject()));
		}

		[Test]
		public void SerializeUnserializableReturnType()
		{
			CreateClientServer();
			var cls = CreateRemoteInstance();
			Assert.Throws<SerializationException>(() => cls.ServerError());
		}

		[Test]
		public void TwoClientsCanConnect()
		{
			AuthenticationInformation authenticationInfo = CreateClientServer();
			var client2 = new Client("localhost", Client.DefaultNetworkPort, authenticationInfo);
			client2.Start();

			var firstInstance = _client.CreateRemoteInstance<MarshallableClass>();
			var secondInstance = client2.CreateRemoteInstance<MarshallableClass>();
			Assert.AreNotEqual(firstInstance, secondInstance);

			// Test that the callback ends on the correct client.
			var cb1 = new CallbackImpl();
			var cb2 = new CallbackImpl();
			firstInstance.RegisterCallback(cb1);
			secondInstance.RegisterCallback(cb2);
			firstInstance.DoCallback();
			Assert.That(cb1.HasBeenCalled);
			Assert.False(cb2.HasBeenCalled);

			client2.Disconnect();
			client2.Dispose();

			// This is still operational
			Assert.That(firstInstance.GetSomeData() != 0);
		}

		[Test]
		public void CanRegisterUnregisterEvents()
		{
			CreateClientServer();
			IMarshallInterface instance = _client.CreateRemoteInstance<MarshallableClass>();

			_dataReceived = null;
			instance.DoCallbackOnEvent("Test string");

			Assert.IsNull(_dataReceived);
			instance.AnEvent += CallbackMethod;
			instance.DoCallbackOnEvent("Another test string");
			Assert.AreEqual("Another test string", _dataReceived);
			_dataReceived = null;
			instance.AnEvent -= CallbackMethod;
			instance.DoCallbackOnEvent("A final test string");
			Assert.IsNull(_dataReceived);
		}

		private void CallbackMethod4(string message, string caller)
		{
			_dataReceived = message + "from" + caller;
		}

		private void CallbackMethod2a(string message, string caller)
		{
			_dataReceived2 = message + "from" + caller;
		}

		[Test]
		[TestCase(false)]
		public void CanRegisterUnregisterEventWithoutAffectingOtherInstance([Values]bool remote)
		{
			CreateClientServer();
			IMarshallInterface instance = CreateInstance(remote, "instance0");
			IMarshallInterface instance2 = CreateInstance(remote, "instance2");
			_dataReceived = null;
			instance.DoCallbackOnEvent("Initial test string");

			Assert.IsNull(_dataReceived);
			instance.AnEvent += CallbackMethod4;
			instance2.AnEvent += CallbackMethod4;
			_dataReceived = null;
			instance.AnEvent -= CallbackMethod4;
			instance.DoCallbackOnEvent("More testing");
			Assert.IsNull(_dataReceived);
			instance2.DoCallbackOnEvent("And yet another test");
			Assert.IsNotNull(_dataReceived);
		}

		[Test]
		[TestCase(false)]
		public void CanRegisterTwoCallbacks([Values] bool remote)
		{
			CreateClientServer();
			IMarshallInterface instance = CreateInstance(remote, "instance0");
			_dataReceived = null;
			instance.DoCallbackOnEvent("Initial test string");

			Assert.IsNull(_dataReceived);
			instance.AnEvent += CallbackMethod4;
			instance.AnEvent += CallbackMethod2a;
			_dataReceived = null;
			_dataReceived2 = null;
			instance.DoCallbackOnEvent("Some test string");
			Assert.IsNotNull(_dataReceived);
			Assert.IsNotNull(_dataReceived2);
			_dataReceived = null;
			_dataReceived2 = null;
			instance.AnEvent -= CallbackMethod4;
			instance.DoCallbackOnEvent("Simple test string");
			Assert.IsNull(_dataReceived);
			Assert.IsNotNull(_dataReceived2);
			instance.AnEvent -= CallbackMethod2a;
		}

		[Test]
		[TestCase(false)]
		public void RegisterForCallbackReverse([Values] bool remote)
		{
			CreateClientServer();
			IMarshallInterface instance = CreateInstance(remote, "instance0");
			_dataReceived = null;

			ICallbackInterface localStuff = new CallbackImpl();
			instance.RegisterForCallback(localStuff);
			localStuff.InvokeCallback("Test");
			instance.EnsureCallbackWasUsed();
		}

		[Test]
		[TestCase(false)]
		public void CanRegisterCallbackOnDifferentInstance([Values] bool remote)
		{
			CreateClientServer();
			IMarshallInterface instance = CreateInstance(remote, "instance0");
			instance.DoCallbackOnEvent("Initial test string");

			EventSink sink = new EventSink();

			instance.AnEvent += sink.RegisterThis;

			instance.DoCallbackOnEvent("Test");
			Assert.AreEqual("Test from instance0", sink.Data);

			sink.Data = null;
			instance.AnEvent -= sink.RegisterThis;
			instance.DoCallbackOnEvent("No test");
			Assert.IsNull(sink.Data);
		}

		[Test]
		[TestCase(false)]
		public void CanRegisterCallbackOnDifferentInstance2([Values] bool remote)
		{
			CreateClientServer();
			IMarshallInterface instance = CreateInstance(remote, "instance0");
			instance.DoCallbackOnEvent("Initial test string");

			EventSink sink = new EventSink();
			EventSink anotherSink = new EventSink();

			instance.AnEvent += sink.RegisterThis;
			instance.AnEvent += anotherSink.RegisterThis;

			instance.DoCallbackOnEvent("Test");
			Assert.AreEqual("Test from instance0", sink.Data);
			Assert.AreEqual("Test from instance0", anotherSink.Data);

			sink.Data = null;
			instance.AnEvent -= sink.RegisterThis;
			instance.DoCallbackOnEvent("Test2");
			Assert.IsNull(sink.Data);
			Assert.AreEqual("Test2 from instance0", anotherSink.Data);
		}

		[Test]
		public void RemovingAnAlreadyRemovedDelegateDoesNothing()
		{
			CreateClientServer();
			EventHandling();
			IMarshallInterface instance = _client.CreateRemoteInstance<MarshallableClass>(nameof(RemovingAnAlreadyRemovedDelegateDoesNothing));

			_dataReceived = null;
			instance.DoCallbackOnEvent("Test string");

			Assert.IsNull(_dataReceived);
			instance.AnEvent += CallbackMethod;
			instance.DoCallbackOnEvent("Another test string");
			Assert.AreEqual("Another test string", _dataReceived);
			_dataReceived = null;
			instance.AnEvent -= CallbackMethod;
			instance.DoCallbackOnEvent("A third test string");
			Assert.IsNull(_dataReceived);
			instance.AnEvent -= CallbackMethod;
			instance.DoCallbackOnEvent("Test string 4");
			Assert.IsNull(_dataReceived);
		}

		/// <summary>
		/// Does the same as above, but with a local target object
		/// </summary>
		[Test]
		public void RemovingAnAlreadyRemovedDelegateDoesNothingLocal()
		{
			IMarshallInterface instance = new MarshallableClass("Test");

			_dataReceived = null;
			instance.DoCallbackOnEvent("Test string");
			Assert.IsNull(_dataReceived);
			instance.AnEvent += CallbackMethod;
			instance.DoCallbackOnEvent("Another test string");
			Assert.AreEqual("Another test string", _dataReceived);
			_dataReceived = null;
			instance.AnEvent -= CallbackMethod;
			instance.DoCallbackOnEvent("A third test string");
			Assert.IsNull(_dataReceived);
			instance.AnEvent -= CallbackMethod;
			instance.DoCallbackOnEvent("Test string 4");
			Assert.IsNull(_dataReceived);
		}

		[Test]
		public void CopyStreamToServer()
		{
			CreateClientServer();
			MemoryStream ms = new MemoryStream();
			byte[] data = new byte[10 * 1024 * 1024];
			data[1] = 2;
			ms.Write(data);
			var server = CreateRemoteInstance();
			Stopwatch sw = Stopwatch.StartNew();
			Assert.True(server.StreamDataContains(ms, 2));
			Debug.WriteLine($"Stream of size {ms.Length} took {sw.Elapsed} to send");
		}

		[Test]
		public void FileStreamFromClientToServer()
		{
			CreateClientServer();
			var server = CreateRemoteInstance();
			string fileToOpen = Assembly.GetExecutingAssembly().Location;
			using FileStream fs = new FileStream(fileToOpen, FileMode.Open, FileAccess.Read);
			Assert.True(server.CheckStreamEqualToFile(fileToOpen, fs.Length, fs));
		}

		[Test]
		public void CreateFileStreamOnRemoteServer()
		{
			CreateClientServer();
			var server = CreateRemoteInstance();
			string fileToOpen = Assembly.GetExecutingAssembly().Location;
			using var stream = server.GetFileStream(fileToOpen);
			Assert.NotNull(stream);
			Assert.True(stream is FileStream);
			FileStream fs = (FileStream)stream;
			Assert.True(fs.Length > 0);
			Assert.IsNotNull(fs.Name);
			Assert.AreEqual('M', fs.ReadByte()); // This is a dll file. It starts with the letters "MZ".
			server.CloseStream(stream);
			fs.Dispose();
		}

		[Test]
		public void CanUseAnArgumentThatIsGeneric()
		{
			CreateClientServer();
			var server = CreateRemoteInstance();
			var list = new List<int>();
			list.Add(1);
			list.Add(2);
			Assert.AreEqual(2, server.ListCount(list));
		}

		[Test]
		public void DistributedGcTest1()
		{
			CreateClientServer();
			var server = CreateRemoteInstance();
			var component = server.GetComponent();
			Assert.NotNull(component);
			component = null;
			GC.Collect();
			GC.WaitForPendingFinalizers();
			_client.ForceGc();
			// This may just recreate a new remote reference, so this test is not very expressive.
			// But we can't keep a reference to the object to check whether it is properly GC'd.
			Assert.NotNull(server.GetComponent());
		}

		[Test]
		public void VerifyMatchingServer()
		{
			CreateClientServer();
			Assert.IsNull(_client.VerifyMatchingServer());
		}

		public void EventHandling()
		{
			int expectedCounter = 0;

			var instance = _client.CreateRemoteInstance<MarshallableClass>();
			instance.DoCallbackOnEvent("Utest");

			ExecuteCallbacks(instance, 4, 10,  ref expectedCounter);
		}

		[Test]
		public void EventHandlingTest()
		{
			CreateClientServer();
			EventHandling();
		}

		[Test]
		public void ParallelGetObjectInstance()
		{
			CreateClientServer();
			var server = _client.CreateRemoteInstance<MarshallableClass>();
			server.CreateCalc();
			SimpleCalc localCalc = new SimpleCalc();
			Parallel.For(0, 100, x =>
			{
				if (x != 0) // Not on the primary thread
				{
					var calc = server.DetermineCalc();
					Assert.NotNull(calc);
					double result = calc.Add(x, x);
					Assert.AreEqual(2 * x, result, 1E-10);

					_client.ForceGc();
					calc.DooFoo(localCalc);
				}
			});
		}

		[Test]
		public void TestSealedProxy()
		{
			CreateClientServer();
			var server = _client.CreateRemoteInstance<MarshallableClass>();
			object instance = server.GetSealedClass();

			IMyComponentInterface interf = (IMyComponentInterface)instance;
			var data = interf.ProcessName();
			IDisposable disp = (IDisposable)interf;
			disp.Dispose();

			Assert.False(string.IsNullOrWhiteSpace(data));
		}

		[Test]
		public void TestFastArgumentPassing()
		{
			CreateClientServer();
			var server = _client.CreateRemoteInstance<MarshallableClass>();
			Assert.True(server.TakeSomeArguments(10, 2000, 2000, 10.0));
		}

		private void ExecuteCallbacks(IMarshallInterface instance, int overallIterations, int iterations,
			ref int expectedCounter)
		{
			for (int j = 0; j < overallIterations; j++)
			{
				instance.AnEvent += CallbackMethod;
				for (int i = 0; i < iterations; i++)
				{
					instance.DoCallbackOnEvent("Utest" + ++expectedCounter);
					Assert.AreEqual("Utest" + expectedCounter, _dataReceived);
				}

				if (j % 2 == 0)
				{
					_client.ForceGc();
				}

				instance.AnEvent -= CallbackMethod;
			}
		}

		private MarshallableClass CreateRemoteInstance()
		{
			return _client.CreateRemoteInstance<MarshallableClass>();
		}

		private IMarshallInterface CreateInstance(bool remote, string name)
		{
			if (remote)
			{
				return _client.CreateRemoteInstance<MarshallableClass>(name);
			}
			else
			{
				return new MarshallableClass(name);
			}
		}

		public void CallbackMethod(string argument, string senderInstance)
		{
			if (!senderInstance.StartsWith("Unnamed"))
			{
				Console.WriteLine($"CallbackMethod: Previous value {_dataReceived}, new value {argument}, sender {senderInstance}");
			}

			_dataReceived = argument;
		}

		private sealed class CallbackImpl : MarshalByRefObject, ICallbackInterface
		{
			public CallbackImpl()
			{
				HasBeenCalled = false;
			}

			public event Action<string> Callback;

			public bool HasBeenCalled { get; set; }

			public void FireSomeAction(string nameOfAction)
			{
				HasBeenCalled = true;
			}

			public void InvokeCallback(string data)
			{
				Callback?.Invoke(data);
			}
		}

		private sealed class EventSink
		{
			public string Data
			{
				get;
				set;
			}

			public void RegisterThis(string msg, string source)
			{
				Data = $"{msg} from {source}";
			}
		}
	}
}
