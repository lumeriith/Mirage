using System.Collections;
using System.Net.Sockets;
using System.Threading.Tasks;
using NSubstitute;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.TestTools;
using static Mirror.Tests.AsyncUtil;

namespace Mirror.Tests
{
    [TestFixture]
    public class NetworkManagerTest
    {
        GameObject gameObject;
        NetworkManager manager;

        IConnection tconn1;
        IConnection tconn2;


        [SetUp]
        public void SetupNetworkManager()
        {

            gameObject = new GameObject();
            gameObject.AddComponent<LoopbackTransport>();
            manager = gameObject.AddComponent<NetworkManager>();
            manager.startOnHeadless = false;
            manager.client = gameObject.GetComponent<NetworkClient>();
            manager.server = gameObject.GetComponent<NetworkServer>();

            (tconn1, tconn2) = PipeConnection.CreatePipe();
        }

        [TearDown]
        public void TearDownNetworkManager()
        {
            Object.DestroyImmediate(gameObject);
        }

        [Test]
        public void VariableTest()
        {
            Assert.That(manager.dontDestroyOnLoad, Is.True);
            Assert.That(manager.startOnHeadless, Is.False);
            Assert.That(manager.showDebugMessages, Is.False);
            Assert.That(manager.serverTickRate, Is.EqualTo(30));
            Assert.That(manager.server.MaxConnections, Is.EqualTo(4));
            Assert.That(manager.IsNetworkActive, Is.False);

            Assert.That(manager.networkSceneName, Is.Empty);
        }

        [UnityTest]
        public IEnumerator StartServerTest()
        {
            return RunAsync(async () =>
            {
                Assert.That(manager.server.active, Is.False);

                await manager.StartServer();

                Assert.That(manager.IsNetworkActive, Is.True);
                Assert.That(manager.server.active, Is.True);

                manager.StopServer();
            });

        }

        [UnityTest]
        public IEnumerator StopServerTest()
        {
            return RunAsync(async () =>
            {
                // wait for NetworkManager to initialize
                await Task.Delay(1);

                await manager.StartServer();
                manager.StopServer();

                // wait for manager to stop
                await Task.Delay(1);

                Assert.That(manager.IsNetworkActive, Is.False);
            });
        }

        [Test]
        public void StartClientTest()
        {
            manager.StartClient("localhost");

            Assert.That(manager.IsNetworkActive, Is.True);

            manager.StopClient();
        }

        [UnityTest]
        public IEnumerator ConnectedClientTest()
        {
            return RunAsync(async () =>
            {
                await manager.StartServer();
                UnityAction<NetworkConnectionToServer> func = Substitute.For<UnityAction<NetworkConnectionToServer>>();
                manager.client.Connected.AddListener(func);

                await manager.client.ConnectAsync(new System.Uri("tcp4://localhost"));
                func.Received().Invoke(Arg.Any<NetworkConnectionToServer>());
                manager.client.Disconnect();
                manager.StopServer();
            });
        }

        [UnityTest]
        public IEnumerator ConnectedClientUriTest()
        {
            return RunAsync(async () =>
            {
                await manager.StartServer();
                UnityAction<NetworkConnectionToServer> func = Substitute.For<UnityAction<NetworkConnectionToServer>>();
                manager.client.Connected.AddListener(func);
                await manager.client.ConnectAsync(new System.Uri("tcp4://localhost"));
                func.Received().Invoke(Arg.Any<NetworkConnectionToServer>());
                manager.client.Disconnect();
                manager.StopServer();
                await Task.Delay(1);

            });
        }

        [UnityTest]
        public IEnumerator ConnectedHostTest()
        {
            return RunAsync(async () =>
            {
                await manager.StartServer();
                UnityAction<NetworkConnectionToServer> func = Substitute.For<UnityAction<NetworkConnectionToServer>>();
                manager.client.Connected.AddListener(func);
                manager.client.ConnectHost(manager.server);
                func.Received().Invoke(Arg.Any<NetworkConnectionToServer>());
                manager.client.Disconnect();
                manager.StopServer();

                await Task.Delay(1);
            });
        }

        [UnityTest]
        public IEnumerator ConnectionRefusedTest()
        {
            return RunAsync(async () =>
            {
                try
                {
                    await manager.StartClient("localhost");
                    Assert.Fail("If server is not available, it should throw exception");
                }
                catch (SocketException)
                {
                    // Good
                }
            });
        }

        [UnityTest]
        public IEnumerator StopClientTest()
        {
            return RunAsync(async () =>
            {
                await manager.StartServer();

                await manager.StartClient("localhost");

                manager.StopClient();
                manager.StopServer();

                // wait until manager shuts down
                await Task.Delay(1);

                Assert.That(manager.IsNetworkActive, Is.False);
            });
        }
    }
}
