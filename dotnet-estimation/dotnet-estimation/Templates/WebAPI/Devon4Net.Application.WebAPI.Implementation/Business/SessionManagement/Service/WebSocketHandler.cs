using Devon4Net.Infrastructure.Logger.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Tasks;

namespace Devon4Net.Application.WebAPI.Implementation.Business.SessionManagement.Service
{
    public class WebSocketHandler : IWebSocketHandler
    {
        private ConcurrentDictionary<long, ConcurrentDictionary<string, WebSocket>> _sessions = new ConcurrentDictionary<long,ConcurrentDictionary<string, WebSocket>>();

        public async Task Handle(Guid id, WebSocket webSocket, long sessionId)
        {
            var clientId = id.ToString();
            //TODO: Delete after implementing a distinct id logic for users.
            Console.WriteLine("Guid inside WebSocketHandler:" + id);
            _sessions.AddOrUpdate(sessionId, sessionId =>
            {
                //No existing lobby for the given sessionId --> create a new lobby and insert the first client
                var lobby = new ConcurrentDictionary<string, WebSocket>();
                lobby.TryAdd(clientId, webSocket);
                // Save the initial lobby for given sessionId to our sessions dictionary.
                _sessions.TryAdd(sessionId, lobby);
                return lobby;
            },
            (_, existingLobby) =>
            {
                //A lobby for the given sessionId exists --> extend the lobby by the recently joined client
                existingLobby.TryAdd(clientId, webSocket);
                return existingLobby;
            });

            while (webSocket.State == WebSocketState.Open)
            {
                var message = await ReceiveMessage(id, webSocket);
                if (message != null)
                    await SendMessageToSockets(message, sessionId);
            }
        }

        public async Task<string> ReceiveMessage(Guid id, WebSocket webSocket)
        {
            var arraySegment = new ArraySegment<byte>(new byte[4096]);
            var receivedMessage = await webSocket.ReceiveAsync(arraySegment, CancellationToken.None);
            if (receivedMessage.MessageType == WebSocketMessageType.Text)
            {
                var message = Encoding.Default.GetString(arraySegment).TrimEnd('\0');
                if (!string.IsNullOrWhiteSpace(message))
                    return $"<b>{id}</b>: {message}";
            }
            //TODO: If Client closes the websocket connection, handle gracefully!
/*            if(receivedMessage.MessageType == WebSocketMessageType.Close)
            {
                await webSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None);
            }*/
            return null;
        }

        public async Task SendMessageToSockets(string message, long sessionId)
        {
            if (_sessions.ContainsKey(sessionId))
            {
                foreach (var connection in _sessions[sessionId])
                {
                    if (connection.Value.State == WebSocketState.Open)
                    {
                        var bytes = Encoding.Default.GetBytes(message);
                        var arraySegment = new ArraySegment<byte>(bytes);
                        await connection.Value.SendAsync(arraySegment, WebSocketMessageType.Text, true, CancellationToken.None);
                    }
                }
            }
        }

        public async Task Send<T>(Message<T> message, long sessionId)
        {
            await SendMessageToSockets(JsonConvert.SerializeObject(message, new JsonSerializerSettings { ContractResolver = new CamelCasePropertyNamesContractResolver() }), sessionId);
        }

        public async Task<(bool,WebSocket)> DeleteWebSocket(long sessionId, string clientId)
        {
            WebSocket socketToRemove;
            ConcurrentDictionary<string, WebSocket> currentLobby;
            //Get the current Lobby of clients
            _sessions.TryGetValue(sessionId, out currentLobby);

            //Delete the client with specified clientId, but keep an instance of the old lobby to compare with, when updating the sessions dictionary.
            var resultLobby = currentLobby;
            var deleted = resultLobby.TryRemove(clientId, out socketToRemove);
            if (!deleted)
            {
                socketToRemove = null;
                Devon4NetLogger.Debug($"The Websocket for client: {clientId} was NOT DELETED!");
            }
            else
            {
                //Initiate a websocket close from server towards client
                await socketToRemove.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None);
            }
            //Update the sessions dictrionary, to free up resources of unused WebSockets
            _sessions.TryUpdate(sessionId, resultLobby, currentLobby);
            return (deleted, socketToRemove);
        }
    }
}
