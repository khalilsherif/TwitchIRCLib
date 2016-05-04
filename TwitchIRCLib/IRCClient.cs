using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;

namespace TwitchIRCLib
{
    public delegate void CommandReceivedDelegate(string[] args, string command, IRCClient irc_client);
    public delegate void FullCommandReceivedDelegate(int callbackIndex, string[] args, string command, IRCClient irc_client);
    public delegate void OnDisconnectDelegate();
    public class IRCClient
    {
        private string _channel;
        private string _username;
        private string _authtoken;

        private Socket _sock;
        private byte[] _buffer = new byte[MAX_BUFFER_SIZE];

        private MemoryStream _memStream;
        private StreamWriter _memWriter;
        private StreamReader _memReader;

        private static string irc_host = "irc.chat.twitch.tv";
        private static int irc_port = 6667;
        private const int MAX_BUFFER_SIZE = 1024 * 2;
        private const int MAX_MESSAGE_LENGTH = 512;

        public string Channel { get { return _channel; } }
        public string Username { get { return _channel; } }
        public string GetOAuthToken { get { return _authtoken; } }
        public bool Connected { get { return _sock.Connected; } }
        public bool DisplayLog { get; set; }

        public Dictionary<string, CommandReceivedDelegate> Callbacks;

        public FullCommandReceivedDelegate MasterCallback { get; set; }
        public OnDisconnectDelegate OnDisconnect { get; set; }

        public IRCClient()
        {
            _memStream = new MemoryStream();
            _memWriter = new StreamWriter(_memStream);
            _memReader = new StreamReader(_memStream);

            Callbacks = new Dictionary<string, CommandReceivedDelegate>();
        }

        public bool Connect()
        {
            _sock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp); 

            _sock.Connect(Dns.GetHostEntry(irc_host).AddressList[0], irc_port);
            if (!_sock.Connected) return false;

            return true;
        }

        public void Login(string username, string authtoken)
        {
            _username = username;
            _authtoken = authtoken;

            string authAnnounce = String.Format("PASS {0}\n", _authtoken);
            string nickAnnounce = String.Format("NICK {0}\n", _username);

            SendCommand(authAnnounce);
            SendCommand(nickAnnounce);
        }
        public void Disconnect()
        {
            if (Connected)
                _sock.Disconnect(false);
            OnDisconnect();
        }

        public void JoinChannel(string channel)
        {
            _channel = channel;
            if (!_channel.StartsWith("#")) _channel = "#" + _channel;
            string joinAnnounce = String.Format("JOIN {0}\n", _channel);
            byte[] bJoin = System.Text.Encoding.UTF8.GetBytes(joinAnnounce);
            _sock.Send(bJoin);
        }

        public void BeginListenAsync()
        {
            _sock.BeginReceive(_buffer, 0, MAX_BUFFER_SIZE, SocketFlags.None, ReadCallback, this);
        }

        internal static void ReadCallback(IAsyncResult ar)
        {
            IRCClient client = ar.AsyncState as IRCClient;

            int nRead = client._sock.EndReceive(ar);

            if (nRead != 0)
            {
                client._memStream.Seek(0, SeekOrigin.End);

                client._memWriter.Write(System.Text.Encoding.UTF8.GetString(client._buffer, 0, nRead));
                client._memWriter.Flush();

                client._memStream.Seek(0, SeekOrigin.Begin);
                string buffer = client._memReader.ReadToEnd();

                int index = buffer.LastIndexOf("\r\n");

                if (index != -1)
                    index += 2;
                else
                    index = 0;

                string remainder = buffer.Substring(index, buffer.Length - index);

                client._memStream.Seek(0, SeekOrigin.Begin);
                client._memWriter.Write(remainder);
                client._memWriter.Flush();
                client._memStream.Seek(0, SeekOrigin.Begin);
                client._memStream.SetLength(buffer.Length - index);

                buffer = buffer.Substring(0, index);

                string[] messages = buffer.Split(new char[] {'\r', '\n'}, StringSplitOptions.RemoveEmptyEntries);

                foreach (string message in messages)
                    client.ProcessMessage(message);

                client._sock.BeginReceive(client._buffer, 0, MAX_BUFFER_SIZE, SocketFlags.None, ReadCallback, client);
            }
            else
            {
                if(client.OnDisconnect != null) client.OnDisconnect();
                client._sock.Disconnect(false);
            }
        }

        private void ProcessMessage(string message)
        {
            string rawMessage = message;
            int callbackIndex = 0;

            int indexedColon = 1;
            if (message.StartsWith(":"))
                message = message.Remove(0, ++callbackIndex);
            else if (message.StartsWith("@"))
            {
                indexedColon = 2;
                callbackIndex = 2;

                string messageSplit = message.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)[0];
                if (messageSplit.Contains(":"))
                {
                    foreach (char i in messageSplit)
                        if (i == ':')
                            indexedColon++;
                }
            }

            string[] args;
            if (message.Contains(":"))
            {
                int index = 0;
                for(int i = 0; i < indexedColon; i++)
                {
                    int oldIndex = index;
                    if (index + 1 > message.Length)
                        break;
                    index = message.IndexOf(":", index + 1);
                    if (index == -1 || oldIndex == index)
                        index = message.Length;
                }

                string[] args1 = message.Substring(0, index).Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                string[] args2;
                if (index < message.Length)
                    args2 = new string[] { message.Substring(index + 1, message.Length - index - 1) };
                else
                    args2 = new string[] { };

                args = new string[args1.Length + args2.Length];
                Array.Copy(args1, args, args1.Length);
                Array.Copy(args2, 0, args, args1.Length, args2.Length);
            }
            else
            {
                args = message.Split(new char[]{' '}, StringSplitOptions.RemoveEmptyEntries);
            }
            if (args.Length > callbackIndex)
            {
                if (MasterCallback != null) MasterCallback(callbackIndex, args, rawMessage, this);

                if (Callbacks.ContainsKey(args[callbackIndex]))
                    Callbacks[args[callbackIndex]](args, rawMessage, this);
            }
            else
            {
                if (MasterCallback != null) MasterCallback(-1, null, rawMessage, this);
            }
        }

        public void SendMessage(string message)
        {
            SendCommand(GetMessageCommand(message));
        }

        public string GetMessageCommand(string message)
        {
            return string.Format("PRIVMSG {0} :{1}", _channel, message);
        }

        public void SendCommand(string command)
        {
            if (!_sock.Connected) return;
            if (!command.EndsWith("\n")) command += '\n';
            if (command.Length > MAX_MESSAGE_LENGTH)
                return;
            byte[] bData = System.Text.Encoding.UTF8.GetBytes(command);
            _sock.Send(bData);
        }
    }
}
