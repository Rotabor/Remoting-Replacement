﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SampleServerClasses
{
	public class WithManyInterfaces : MarshalByRefObject, IMarshallInterface, IDisposable, IEnumerable<int>
	{
		public event Action<string> AnEvent;
		public string StringProcessId()
		{
			return "SomeString";
		}

		public void DoCallbackOnEvent(string msg)
		{
		}

		public void Dispose()
		{
		}

		public IEnumerator<int> GetEnumerator()
		{
			return new List<int>.Enumerator();
		}

		IEnumerator IEnumerable.GetEnumerator()
		{
			return GetEnumerator();
		}

		public virtual void FireEvent()
		{
			AnEvent?.Invoke("Fire!");
		}
	}
}
