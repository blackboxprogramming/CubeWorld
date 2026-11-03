using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using System.Net;
using System.Net.Sockets;
using CubeWorld.Console;

namespace CubeWorld.Gameplay.Multiplayer
{
    public class MultiplayerServer
    {
        private TcpListener serverSocket;
        private List<MultiplayerClient> clients = new List<MultiplayerClient>();

        private IMultiplayerServerListener serverListener;
        private IMultiplayerClientListener clientListener;

        public int Port
        {
            get { return ((IPEndPoint)serverSocket.LocalEndpoint).Port; }
        }

        public MultiplayerServer(int port, IMultiplayerServerListener serverListener, IMultiplayerClientListener clientListener)
            : this(IPAddress.Any, port, serverListener, clientListener)
        {
        }

        public MultiplayerServer(IPAddress bindAddress, int port, IMultiplayerServerListener serverListener, IMultiplayerClientListener clientListener)
        {
            this.serverListener = serverListener;
            this.clientListener = clientListener;
            serverSocket = new TcpListener(bindAddress, port);
            serverSocket.Start();
        }

        public void ClientAccepted(MultiplayerClient client, bool isPolicy)
        {
            if (isPolicy == false)
                serverListener.ClientConnected(client);
        }

        public void Update()
        {
            try
            {
                if (serverSocket.Pending())
                {
                    MultiplayerClient client = new MultiplayerClient(serverSocket.AcceptTcpClient(), this, clientListener);
                    clients.Add(client);
                }
            }
            catch (Exception ex)
            {
                CWConsole.LogError(ex.ToString());
            }

            List<MultiplayerClient> clientsToRemove = new List<MultiplayerClient>();

            foreach (MultiplayerClient client in clients)
            {
                try
                {
                    if (client.Update(999) == false)
                        clientsToRemove.Add(client);
                }
                catch (Exception ex)
                {
                    clientsToRemove.Add(client);
                    CWConsole.LogError(ex.ToString());
                }
            }

            foreach (MultiplayerClient client in clientsToRemove)
            {
                try
                {
                    client.Clear();
                }
                catch (Exception ex)
                {
                    CWConsole.LogError(ex.ToString());
                }
                clients.Remove(client);

                if (client.id >= 0)
                    serverListener.ClientDisconnected(client);
            }
        }

        public void SendToEveryone(MultiplayerAction action, int idClientToIgnore)
        {
            foreach (MultiplayerClient client in clients)
                if (client.id != idClientToIgnore && client.id >= 0)
                    client.AddAction(action);
        }

        public void SendToOne(MultiplayerAction action, int idClient)
        {
            foreach (MultiplayerClient client in clients)
            {
                if (client.id == idClient)
                {
                    client.AddAction(action);
                    break;
                }
            }
        }

        public void Clear()
        {
            foreach (MultiplayerClient client in clients)
            {
                try
                {
                    client.Clear();
                }
                catch (Exception)
                {
                }
            }

            clients.Clear();

            serverSocket.Stop();
        }
    }
}
