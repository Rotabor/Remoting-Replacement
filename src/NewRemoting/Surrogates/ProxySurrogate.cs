﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.Json;
using System.Threading.Tasks;

namespace NewRemoting.Surrogates
{
	internal sealed class ProxySurrogate : JsonConverter<object>
	{
		private readonly IInstanceManager _instanceManager;
		private readonly string _otherSideInstanceId;

		public ProxySurrogate(IInstanceManager instanceManager, string otherSideInstanceId)
		{
			_instanceManager = instanceManager;
			_otherSideInstanceId = otherSideInstanceId;
		}

		public override bool CanConvert(Type typeToConvert)
		{
			return MessageHandler.IsMarshalByRefType(typeToConvert);
		}

		public override void Write(Utf8JsonWriter writer, object value, JsonSerializerOptions options)
		{
			string objectId;
			if (Client.IsRemoteProxy(value))
			{
				// This should have an unit test, but I have not yet found out what test code causes this situation
				if (!_instanceManager.TryGetObjectId(value, out objectId, out _))
				{
					throw new RemotingException("Couldn't find matching objectId, although should be there");
				}

				writer.WriteStartObject();
				writer.WriteString("AssemblyQualifiedName", string.Empty);
				writer.WriteString("ObjectId", objectId);
				writer.WriteEndObject();
			}
			else
			{
				objectId = _instanceManager.RegisterRealObjectAndGetId(value, _otherSideInstanceId);
				writer.WriteStartObject();
				writer.WriteString("AssemblyQualifiedName", value.GetType().AssemblyQualifiedName);
				writer.WriteString("ObjectId", objectId);
				writer.WritePropertyName("Interfaces");
				writer.WriteStartArray();
				foreach (var elem in value.GetType().GetInterfaces().Where(x => x.IsPublic))
				{
					writer.WriteStringValue(elem.AssemblyQualifiedName);
				}

				writer.WriteEndArray();
				writer.WriteEndObject();
			}
		}

		public override object Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
		{
			if (reader.TokenType != JsonTokenType.StartObject)
			{
				throw new JsonException($"Expected object, got {reader.TokenType}");
			}

			string objectId = null;
			string typeName = null;
			List<string> interfaceList = new List<string>();
			while (reader.Read())
			{
				if (reader.TokenType == JsonTokenType.EndObject)
				{
					break;
				}

				string propertyName = reader.GetString()!;
				reader.Read();
				if (propertyName.Equals("ObjectId", StringComparison.Ordinal))
				{
					objectId = reader.GetString();
				}
				else if (propertyName.Equals("AssemblyQualifiedName", StringComparison.Ordinal))
				{
					typeName = reader.GetString();
				}
				else if (propertyName.Equals("Interfaces", StringComparison.Ordinal) && reader.TokenType == JsonTokenType.StartArray)
				{
					while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
					{
						interfaceList.Add(reader.GetString());
					}
				}
				else
				{
					throw new JsonException($"Unknown property {propertyName} seen");
				}
			}

			if (objectId == null || typeName == null)
			{
				throw new JsonException("Invalid reference found");
			}

			// We don't know better here. We do not know what the static type of the field is that will store this reference.
			object newProxy;
			if (!string.IsNullOrEmpty(typeName))
			{
				Type targetType = Server.GetTypeFromAnyAssembly(typeName);
				newProxy = _instanceManager.CreateOrGetProxyForObjectId(true, targetType, typeName, objectId, interfaceList);
			}
			else
			{
				newProxy = _instanceManager.CreateOrGetProxyForObjectId(true, null, typeName, objectId, interfaceList);
			}

			return newProxy;
		}
	}
}