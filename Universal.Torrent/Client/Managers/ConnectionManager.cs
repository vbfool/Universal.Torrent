//
// ConnectionManager.cs
//
// Authors:
//   Alan McGovern alan.mcgovern@gmail.com
//
// Copyright (C) 2006 Alan McGovern
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
// 
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//


using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Universal.Torrent.Client.Args;
using Universal.Torrent.Client.Encryption;
using Universal.Torrent.Client.Encryption.IEncryption;
using Universal.Torrent.Client.Messages;
using Universal.Torrent.Client.Messages.StandardMessages;
using Universal.Torrent.Client.PeerConnections;
using Universal.Torrent.Client.Peers;
using Universal.Torrent.Common;

namespace Universal.Torrent.Client.Managers
{
    internal delegate void MessagingCallback(PeerId id);

    /// <summary>
    ///     Main controller class for all incoming and outgoing connections
    /// </summary>
    public class ConnectionManager
    {
        #region Constructors

        /// <summary>
        ///     Initializes a new instance of the <see cref="ConnectionManager" /> class.
        /// </summary>
        /// <param name="engine">The engine.</param>
        public ConnectionManager(ClientEngine engine)
        {
            _engine = engine;

            _endCheckEncryptionCallback = ClientEngine.MainLoop.Wrap(EndCheckEncryption);
            _endSendMessageCallback = (a, b, c) => ClientEngine.MainLoop.Queue(() => EndSendMessage(a, b, c));
            _endCreateConnectionCallback = (a, b, c) => ClientEngine.MainLoop.Queue(() => EndCreateConnection(a, b, c));
            IncomingConnectionAcceptedCallback =
                (a, b, c) => ClientEngine.MainLoop.Queue(() => IncomingConnectionAccepted(a, b, c));

            _handshakeSentCallback = PeerHandshakeSent;
            _peerHandshakeReceivedCallback =
                (a, b, c) => ClientEngine.MainLoop.Queue(() => PeerHandshakeReceived(a, b, c));
            _messageSentCallback = PeerMessageSent;
            MessageReceivedCallback = (a, b, c) => ClientEngine.MainLoop.Queue(() => MessageReceived(a, b, c));

            _pendingConnects = new List<AsyncConnectState>();
        }

        #endregion

        #region Events

        public event EventHandler<AttemptConnectionEventArgs> BanPeer;

        /// <summary>
        ///     Event that's fired every time a message is sent or Received from a Peer
        /// </summary>
        public event EventHandler<PeerMessageEventArgs> PeerMessageTransferred;

        #endregion

        #region Member Variables

        private readonly ClientEngine _engine;

        internal static readonly int ChunkLength = 2096 + 64;
        // Download in 2kB chunks to allow for better rate limiting

        // Create the callbacks and reuse them. Reduces ongoing allocations by a fair few megs
        private readonly AsyncMessageReceivedCallback _peerHandshakeReceivedCallback;
        private readonly MessagingCallback _handshakeSentCallback;
        private readonly MessagingCallback _messageSentCallback;

        private readonly AsyncCallback _endCheckEncryptionCallback;
        private readonly AsyncIOCallback _endCreateConnectionCallback;
        internal AsyncIOCallback IncomingConnectionAcceptedCallback;
        private readonly AsyncIOCallback _endSendMessageCallback;
        internal AsyncMessageReceivedCallback MessageReceivedCallback;

        private readonly List<AsyncConnectState> _pendingConnects;

        /// <summary>
        ///     The number of half open connections
        /// </summary>
        public int HalfOpenConnections => NetworkIO.HalfOpens;


        /// <summary>
        ///     The maximum number of half open connections
        /// </summary>
        public int MaxHalfOpenConnections => _engine.Settings.GlobalMaxHalfOpenConnections;


        /// <summary>
        ///     The number of open connections
        /// </summary>
        public int OpenConnections
        {
            get
            {
                return (int) ClientEngine.MainLoop.QueueWait(() =>
                    (int) Toolbox.Accumulate(_engine.Torrents, m =>
                        m.Peers.ConnectedPeers.Count
                        )
                    );
            }
        }


        /// <summary>
        ///     The maximum number of open connections
        /// </summary>
        public int MaxOpenConnections => _engine.Settings.GlobalMaxConnections;

        private int TryConnectIndex { get; set; }

        #endregion

        #region Async Connection Methods

        internal void ConnectToPeer(TorrentManager manager, Peer peer)
        {
            // Connect to the peer.
            var connection = ConnectionFactory.Create(peer.ConnectionUri);
            if (connection == null)
                return;

            peer.LastConnectionAttempt = DateTime.Now;
            var c = new AsyncConnectState(manager, peer, connection);
            _pendingConnects.Add(c);

            manager.Peers.ConnectingToPeers.Add(peer);
            NetworkIO.EnqueueConnect(connection, _endCreateConnectionCallback, c);
        }

        private void EndCreateConnection(bool succeeded, int count, object state)
        {
            var connect = (AsyncConnectState) state;
            _pendingConnects.Remove(connect);
            if (connect.Manager.Engine == null ||
                !connect.Manager.Mode.CanAcceptConnections)
            {
                connect.Connection.Dispose();
                return;
            }

            try
            {
                connect.Manager.Peers.ConnectingToPeers.Remove(connect.Peer);
                if (!succeeded)
                {
                    Debug.WriteLine("ConnectionManager - Failed to connect {0}", connect.Peer);

                    connect.Manager.RaiseConnectionAttemptFailed(
                        new PeerConnectionFailedEventArgs(connect.Manager, connect.Peer, Direction.Outgoing,
                            "EndCreateConnection"));

                    connect.Peer.FailedConnectionAttempts++;
                    connect.Connection.Dispose();
                    connect.Manager.Peers.BusyPeers.Add(connect.Peer);
                }
                else
                {
                    var id = new PeerId(connect.Peer, connect.Manager) {Connection = connect.Connection};
                    connect.Manager.Peers.ActivePeers.Add(connect.Peer);

                    Debug.WriteLine("ConnectionManager - Connection opened {0}", id.Connection);

                    ProcessFreshConnection(id);
                }
            }

            catch (Exception)
            {
                // FIXME: Do nothing now?
            }
            finally
            {
                // Try to connect to another peer
                TryConnect();
            }
        }

        internal void ProcessFreshConnection(PeerId id)
        {
            // If we have too many open connections, close the connection
            if (OpenConnections > MaxOpenConnections)
            {
                CleanupSocket(id, "Too many connections");
                return;
            }

            try
            {
                id.ProcessingQueue = true;
                // Increase the count of the "open" connections
                EncryptorFactory.BeginCheckEncryption(id, 0, _endCheckEncryptionCallback, id);

                id.TorrentManager.Peers.ConnectedPeers.Add(id);
                id.WhenConnected = DateTime.Now;
                // Baseline the time the last block was received
                id.LastBlockReceived = DateTime.Now;
            }
            catch (Exception)
            {
                id.TorrentManager.RaiseConnectionAttemptFailed(
                    new PeerConnectionFailedEventArgs(id.TorrentManager, id.Peer, Direction.Outgoing,
                        "ProcessFreshConnection: failed to encrypt"));

                id.Connection.Dispose();
                id.Connection = null;
            }
        }

        private void EndCheckEncryption(IAsyncResult result)
        {
            var id = (PeerId) result.AsyncState;
            try
            {
                byte[] initialData;
                EncryptorFactory.EndCheckEncryption(result, out initialData);
                if (initialData != null && initialData.Length > 0)
                    throw new EncryptionException("unhandled initial data");

                var e = _engine.Settings.AllowedEncryption;
                if (id.Encryptor is RC4 && !Toolbox.HasEncryption(e, EncryptionTypes.RC4Full) ||
                    id.Encryptor is RC4Header && !Toolbox.HasEncryption(e, EncryptionTypes.RC4Header) ||
                    id.Encryptor is PlainTextEncryption && !Toolbox.HasEncryption(e, EncryptionTypes.PlainText))
                {
                    CleanupSocket(id, id.Encryptor.GetType().Name + " encryption is not enabled");
                }
                else
                {
                    // Create a handshake message to send to the peer
                    var handshake = new HandshakeMessage(id.TorrentManager.InfoHash, _engine.PeerId,
                        VersionInfo.ProtocolStringV100);
                    SendMessage(id, handshake, _handshakeSentCallback);
                }
            }
            catch
            {
                id.Peer.Encryption &= ~EncryptionTypes.RC4Full;
                id.Peer.Encryption &= ~EncryptionTypes.RC4Header;
                CleanupSocket(id, "Failed encryptor check");
            }
        }

        private void EndSendMessage(bool succeeded, int count, object state)
        {
            var id = (PeerId) state;
            if (!succeeded)
            {
                CleanupSocket(id, "Could not send message");
                return;
            }

            try
            {
                // Invoke the callback which we were told to invoke after we sent this message
                id.MessageSentCallback(id);
            }
            catch (Exception)
            {
                CleanupSocket(id, "Could not send message");
            }
        }

        private void PeerHandshakeSent(PeerId id)
        {
            PeerIO.EnqueueReceiveHandshake(id.Connection, id.Decryptor, _peerHandshakeReceivedCallback, id);
        }

        private void PeerHandshakeReceived(bool succeeded, PeerMessage message, object state)
        {
            var id = (PeerId) state;
            if (!succeeded)
            {
                CleanupSocket(id, "Handshaking failed");
                return;
            }

            try
            {
                message.Handle(id);

                // If there are any pending messages, send them otherwise set the queue
                // processing as finished.
                if (id.QueueLength > 0)
                    id.ConnectionManager.ProcessQueue(id);
                else
                    id.ProcessingQueue = false;

                PeerIO.EnqueueReceiveMessage(id.Connection, id.Decryptor, id.TorrentManager.DownloadLimiter, id.Monitor,
                    id.TorrentManager, MessageReceivedCallback, id);
                // Alert the engine that there is a new usable connection
                id.TorrentManager.HandlePeerConnected(id, Direction.Outgoing);
            }
            catch (TorrentException ex)
            {
                CleanupSocket(id, ex.Message);
            }
        }

        private void PeerMessageSent(PeerId id)
        {
            // If the peer has been cleaned up, just return.
            if (id.Connection == null)
                return;

            // Fire the event to let the user know a message was sent
            RaisePeerMessageTransferred(new PeerMessageEventArgs(id.TorrentManager, id.CurrentlySendingMessage,
                Direction.Outgoing, id));

            id.LastMessageSent = DateTime.Now;
            ProcessQueue(id);
        }

        private void SendMessage(PeerId id, PeerMessage message, MessagingCallback callback)
        {
            try
            {
                id.MessageSentCallback = callback;
                id.CurrentlySendingMessage = message;

                var limiter = id.TorrentManager.UploadLimiter;

                var pieceMessage = message as PieceMessage;
                if (pieceMessage != null)
                {
                    PeerIO.EnqueueSendMessage(id.Connection, id.Encryptor, pieceMessage, limiter, id.Monitor,
                        id.TorrentManager.Monitor, _endSendMessageCallback, id);
                    ClientEngine.BufferManager.FreeBuffer(ref pieceMessage.Data);
                    id.IsRequestingPiecesCount--;
                }
                else
                    PeerIO.EnqueueSendMessage(id.Connection, id.Encryptor, message, null, id.Monitor,
                        id.TorrentManager.Monitor, _endSendMessageCallback, id);
            }
            catch (Exception ex)
            {
                CleanupSocket(id, ex.Message);
            }
        }

        #endregion

        #region Methods

        internal void AsyncCleanupSocket(PeerId id, bool localClose, string message)
        {
            if (id == null) // Sometimes onEncryptoError will fire with a null id
                return;

            try
            {
                // It's possible the peer could be in an async send *and* receive and so end up
                // in this block twice. This check makes sure we don't try to double dispose.
                if (id.Connection == null)
                    return;

                // We can reuse this peer if the connection says so and it's not marked as inactive
                var canResuse = id.Connection.CanReconnect &&
                                !id.TorrentManager.InactivePeerManager.InactivePeerList.Contains(id.Uri);
                Debug.WriteLine(id.Connection, "Cleanup Reason : " + message);

                Debug.WriteLine(id.Connection, "*******Cleaning up*******");
                id.TorrentManager.PieceManager.Picker.CancelRequests(id);
                id.Peer.CleanedUpCount++;

                id.PeerExchangeManager?.Dispose();

                if (!id.AmChoking)
                    id.TorrentManager.UploadingTo--;

                id.Connection.Dispose();
                id.Connection = null;

                id.TorrentManager.Peers.ConnectedPeers.RemoveAll(other => Equals(id, other));

                if (id.TorrentManager.Peers.ActivePeers.Contains(id.Peer))
                    id.TorrentManager.Peers.ActivePeers.Remove(id.Peer);

                // If we get our own details, this check makes sure we don't try connecting to ourselves again
                if (canResuse && id.Peer.PeerId != _engine.PeerId)
                {
                    if (!id.TorrentManager.Peers.AvailablePeers.Contains(id.Peer) && id.Peer.CleanedUpCount < 5)
                        id.TorrentManager.Peers.AvailablePeers.Insert(0, id.Peer);
                }
            }

            finally
            {
                id.TorrentManager.RaisePeerDisconnected(
                    new PeerConnectionEventArgs(id.TorrentManager, id, Direction.None, message));
            }
        }

        internal void CancelPendingConnects(TorrentManager manager)
        {
            foreach (var c in _pendingConnects.Where(c => Equals(c.Manager, manager)))
                c.Connection.Dispose();
        }

        /// <summary>
        ///     This method is called when a connection needs to be closed and the resources for it released.
        /// </summary>
        /// <param name="id">The peer whose connection needs to be closed</param>
        /// <param name="message">The message.</param>
        internal void CleanupSocket(PeerId id, string message)
        {
            ClientEngine.MainLoop.Queue(delegate { id.ConnectionManager.AsyncCleanupSocket(id, true, message); });
        }


        /// <summary>
        ///     This method is called when the ClientEngine recieves a valid incoming connection
        /// </summary>
        /// <param name="succeeded">if set to <c>true</c> [succeeded].</param>
        /// <param name="count">The count.</param>
        /// <param name="state">The state.</param>
        private void IncomingConnectionAccepted(bool succeeded, int count, object state)
        {
            var id = (PeerId) state;

            try
            {
                if (!succeeded)
                {
                    var args = new PeerConnectionFailedEventArgs(id.TorrentManager, id.Peer, Direction.Incoming,
                        "Incoming connection coult not be accepted");
                    id.TorrentManager.RaiseConnectionAttemptFailed(args);
                }

                var maxAlreadyOpen = OpenConnections >=
                                     Math.Min(MaxOpenConnections, id.TorrentManager.Settings.MaxConnections);
                if (!succeeded || id.Peer.PeerId == _engine.PeerId || maxAlreadyOpen)
                {
                    CleanupSocket(id, "Connection was not accepted");
                    return;
                }

                if (id.TorrentManager.Peers.ActivePeers.Contains(id.Peer))
                {
                    Debug.WriteLine(id.Connection, "ConnectionManager - Already connected to peer");
                    id.Connection.Dispose();
                    return;
                }

                Debug.WriteLine(id.Connection, "ConnectionManager - Incoming connection fully accepted");
                id.TorrentManager.Peers.AvailablePeers.Remove(id.Peer);
                id.TorrentManager.Peers.ActivePeers.Add(id.Peer);
                id.TorrentManager.Peers.ConnectedPeers.Add(id);
                id.WhenConnected = DateTime.Now;
                // Baseline the time the last block was received
                id.LastBlockReceived = DateTime.Now;

                id.TorrentManager.HandlePeerConnected(id, Direction.Incoming);

                // We've sent our handshake so begin our looping to receive incoming message
                PeerIO.EnqueueReceiveMessage(id.Connection, id.Decryptor, id.TorrentManager.DownloadLimiter, id.Monitor,
                    id.TorrentManager, MessageReceivedCallback, id);
            }
            catch (Exception e)
            {
                CleanupSocket(id, e.Message);
            }
        }


        private void MessageReceived(bool successful, PeerMessage message, object state)
        {
            var id = (PeerId) state;
            if (!successful)
            {
                id.ConnectionManager.CleanupSocket(id, "Could not receive a message");
                return;
            }

            try
            {
                var e = new PeerMessageEventArgs(id.TorrentManager, message, Direction.Incoming, id);
                id.ConnectionManager.RaisePeerMessageTransferred(e);

                message.Handle(id);

                id.LastMessageReceived = DateTime.Now;
                PeerIO.EnqueueReceiveMessage(id.Connection, id.Decryptor, id.TorrentManager.DownloadLimiter, id.Monitor,
                    id.TorrentManager, MessageReceivedCallback, id);
            }
            catch (TorrentException ex)
            {
                id.ConnectionManager.CleanupSocket(id, ex.Message);
            }
        }

        /// <param name="id">The peer whose message queue you want to start processing</param>
        internal void ProcessQueue(PeerId id)
        {
            if (id.QueueLength == 0)
            {
                id.ProcessingQueue = false;
                return;
            }

            var msg = id.Dequeue();
            var message = msg as PieceMessage;
            if (message != null)
            {
                using (var handle = new ManualResetEvent(false))
                {
                    var pm = message;
                    pm.Data = BufferManager.EmptyBuffer;
                    ClientEngine.BufferManager.GetBuffer(ref pm.Data, pm.ByteLength);
                    _engine.DiskManager.QueueRead(id.TorrentManager,
                        pm.StartOffset + ((long) pm.PieceIndex*id.TorrentManager.Torrent.PieceLength), pm.Data,
                        pm.RequestLength, delegate { handle.Set(); });
                    handle.WaitOne();
                    id.PiecesSent++;
                }
            }
            try
            {
                SendMessage(id, msg, _messageSentCallback);
            }
            catch (Exception e)
            {
                CleanupSocket(id, "Exception calling SendMessage: " + e.Message);
            }
        }

        internal void RaisePeerMessageTransferred(PeerMessageEventArgs e)
        {
            if (PeerMessageTransferred == null)
                return;

            Task.Factory.StartNew(() =>
            {
                var h = PeerMessageTransferred;
                if (h == null)
                    return;

                if (!(e.Message is MessageBundle))
                {
                    h(e.TorrentManager, e);
                }
                else
                {
                    // Message bundles are only a convience for internal usage!
                    var b = (MessageBundle) e.Message;
                    foreach (
                        var args in
                            b.Messages.Select(
                                message => new PeerMessageEventArgs(e.TorrentManager, message, e.Direction, e.ID)))
                    {
                        h(args.TorrentManager, args);
                    }
                }
            });
        }

        internal bool ShouldBanPeer(Peer peer)
        {
            if (BanPeer == null)
                return false;

            var e = new AttemptConnectionEventArgs(peer);
            BanPeer(this, e);
            return e.BanPeer;
        }

        internal void TryConnect()
        {
            // If we have already reached our max connections globally, don't try to connect to a new peer
            while (OpenConnections < MaxOpenConnections && HalfOpenConnections < MaxHalfOpenConnections)
            {
                // Check each torrent manager in turn to see if they have any peers we want to connect to
                for (var i = TryConnectIndex; i < _engine.Torrents.Count; i ++)
                {
                    if (TryConnect(_engine.Torrents[i]))
                    {
                        TryConnectIndex = (i + 1)%_engine.Torrents.Count;
                    }
                }

                TryConnectIndex = 0;
                break;
            }
        }

        private bool TryConnect(TorrentManager manager)
        {
            int i;
            if (!manager.Mode.CanAcceptConnections)
                return false;

            // If we have reached the max peers allowed for this torrent, don't connect to a new peer for this torrent
            if (manager.Peers.ConnectedPeers.Count >= manager.Settings.MaxConnections)
                return false;

            // If the torrent isn't active, don't connect to a peer for it
            if (!manager.Mode.CanAcceptConnections)
                return false;

            // If we are not seeding, we can connect to anyone. If we are seeding, we should only connect to a peer
            // if they are not a seeder.
            for (i = 0; i < manager.Peers.AvailablePeers.Count; i++)
                if (manager.Mode.ShouldConnect(manager.Peers.AvailablePeers[i]))
                    break;

            // If this is true, there were no peers in the available list to connect to.
            if (i == manager.Peers.AvailablePeers.Count)
                return false;

            // Remove the peer from the lists so we can start connecting to him
            var peer = manager.Peers.AvailablePeers[i];
            manager.Peers.AvailablePeers.RemoveAt(i);

            if (ShouldBanPeer(peer))
                return false;

            // Connect to the peer
            ConnectToPeer(manager, peer);
            return true;
        }

        #endregion
    }
}