// DO NOT EDIT: GENERATED BY ZigZagTestGenerator.cs

using System;
using System.Collections;
using Mirage.Serialization;
using Mirage.Tests.Runtime.ClientServer;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Mirage.Tests.Runtime.Generated.ZigZagAttributeTests
{
    
    public class ZigZagBehaviour_int_10 : NetworkBehaviour
    {
        [BitCount(10), ZigZagEncode]
        [SyncVar] public int myValue;

        public event Action<int> onRpc;

        [ClientRpc]
        public void RpcSomeFunction([BitCount(10), ZigZagEncode] int myParam)
        {
            onRpc?.Invoke(myParam);
        }
    }
    public class ZigZagTest_int_10 : ClientServerSetup<ZigZagBehaviour_int_10>
    {
        const int value = 100;

        [Test]
        public void SyncVarIsBitPacked()
        {
            serverComponent.myValue = value;

            using (PooledNetworkWriter writer = NetworkWriterPool.GetWriter())
            {
                serverComponent.SerializeSyncVars(writer, true);

                Assert.That(writer.BitPosition, Is.EqualTo(10));

                using (PooledNetworkReader reader = NetworkReaderPool.GetReader(writer.ToArraySegment()))
                {
                    clientComponent.DeserializeSyncVars(reader, true);
                    Assert.That(reader.BitPosition, Is.EqualTo(10));

                    Assert.That(clientComponent.myValue, Is.EqualTo(value));
                }
            }
        }

        // [UnityTest]
        // [Ignore("Rpc not supported yet")]
        public IEnumerator RpcIsBitPacked()
        {
            int called = 0;
            clientComponent.onRpc += (v) => { called++; Assert.That(v, Is.EqualTo(value)); };

            client.MessageHandler.UnregisterHandler<RpcMessage>();
            int payloadSize = 0;
            client.MessageHandler.RegisterHandler<RpcMessage>((player, msg) =>
            {
                // store value in variable because assert will throw and be catch by message wrapper
                payloadSize = msg.payload.Count;
                clientObjectManager.OnRpcMessage(msg);
            });


            serverComponent.RpcSomeFunction(value);
            yield return null;
            Assert.That(called, Is.EqualTo(1));
            Assert.That(payloadSize, Is.EqualTo(2), $"10 bits is 2 bytes in payload");
        }
    }
}
