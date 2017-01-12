#region -- copyright --
//
// Licensed under the EUPL, Version 1.1 or - as soon they will be approved by the
// European Commission - subsequent versions of the EUPL(the "Licence"); You may
// not use this work except in compliance with the Licence.
//
// You may obtain a copy of the Licence at:
// http://ec.europa.eu/idabc/eupl
//
// Unless required by applicable law or agreed to in writing, software distributed
// under the Licence is distributed on an "AS IS" basis, WITHOUT WARRANTIES OR
// CONDITIONS OF ANY KIND, either express or implied. See the Licence for the
// specific language governing permissions and limitations under the Licence.
//
#endregion
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using NeoECoS.Data;

namespace NeoECoSTest
{
	///////////////////////////////////////////////////////////////////////////////
	/// <summary></summary>
	internal sealed class EngineMock : INotifyPropertyChanged
	{
		public event PropertyChangedEventHandler PropertyChanged;

		private CommandStationMock cs;
		private readonly int id;
		private readonly string name;
		private byte speed;

		private bool? func1 = false;
		private bool? func2 = false;

		public EngineMock(CommandStationMock cs, int id, string name, byte speed)
		{
			this.cs = cs;
			this.id = id;
			this.name = name;
			this.speed = speed;
		} // ctor

		private void OnPropertyChanged(string propertyName)
		{
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
		} // proc OnPropertyChanged

		public int Id => id;

		public string Name => name;

		public byte Speed
		{
			get { return speed; }
			set
			{
				if (speed != value)
				{
					speed = value;
					cs.NotifyEvent(id, new CsOption("Spped", speed));
					OnPropertyChanged(nameof(Speed));
				}
			}
		} // prop Speed

		public bool? Func1
		{
			get { return func1; }
			set
			{
				if (func1 != value)
				{
					func1 = value;
					cs.NotifyEvent(id, new CsOption("func", 0, func1.HasValue && func1.Value ? 1 : 0));
					OnPropertyChanged(nameof(Func1));
				}
			}
		} // prop Func1

		public bool? Func2
		{
			get { return func2; }
			set
			{
				if (func2 != value)
				{
					func2 = value;
					cs.NotifyEvent(id, new CsOption("func", 1, func2.HasValue && func2.Value ? 1 : 0));
					OnPropertyChanged(nameof(Func2));
				}
			}
		} // prop Func2
	} // class EngineMock

	internal sealed class ClientEndPoint
	{
		private static Encoding utf8 = new UTF8Encoding(false);

		private readonly CommandStationMock cs;
		private readonly Socket socket;
		private readonly NetworkStream stream;
		private readonly StreamReader sr;
		private readonly StreamWriter sw;
		private readonly Task reader;

		public ClientEndPoint(CommandStationMock cs, Socket socket)
		{
			this.cs = cs;
			this.socket = socket;
			this.stream = new NetworkStream(socket, true);

			this.sr = new StreamReader(stream, utf8);
			this.sw = new StreamWriter(stream, utf8);
			//sw.NewLine = "\n";
			
			this.reader = Task.Run(ReadSocketAsync);

			Out("Connected.");
		} // ctor

		private async Task ReadSocketAsync()
		{
			try
			{
				while (true)
				{
					var line = await sr.ReadLineAsync();
					if (line == null)
					{
						Out("Socket Closed.");
						cs.RemoveClient(this);
						return;
					}
					else
					{
						Out($"Line: {line}");
						try
						{
							await HandleCommandAsync(line);
						}
						catch (Exception e)
						{
							Debug.Print(e.ToString());
							await sw.WriteLineAsync("<REPLY " + line + ">");
							await sw.WriteLineAsync("<END 1 (" + e.Message.Replace('(', ' ').Replace(')', ' ') + ")>");
						}

						await sw.FlushAsync();
					}
				}
			}
			catch (SocketException e)
			{
				cs.RemoveClient(this);
			}
		} // proc ReadSocket

		private async Task HandleCommandAsync(string line)
		{
			// parse command
			var p = line.IndexOf('(');
			if (p == -1)
			{
				Out("Invalid Command");
				return;
			}

			var command = line.Substring(0, p++);
			var id = Convert.ToInt32(CsOption.ParseValue(line, ref p));
			var options = new List<CsOption>();

			if (line[p] == ',')
			{
				while (true)
				{
					p++;
					options.Add(CsOption.Parse(line, ref p));
					if (p < line.Length)
					{
						if (line[p] == ')')
							break;
						else if (line[p] != ',')
							throw new Exception("Command parsing failed.");
					}
					else
						throw new Exception("Command parsing failed.");
				}
			}

			switch (id)
			{
				case 1:
					await HandleCommandStationCommandAsync(line, command, options);
					break;
				case 10:
					await HandleEngineListCommandAsync(line, command, options);
					break;
				default:
					var engine = cs.Engines.FirstOrDefault(c => c.Id == id);
					if (engine == null)
						throw new ArgumentException($"{id}:{command} not implemented.");

					await HandleEngineRequest(line, command, options);

					break;
			}
		} // proc HandleCommandAsync

		private async Task HandleCommandStationCommandAsync(string line,string command, List<CsOption> options)
		{
			switch (command)
			{
				case "get":
					await sw.WriteLineAsync("<REPLY " + line + ">");
					foreach (var o in options)
					{
						switch (o.Name)
						{
							case "info":
								await sw.WriteLineAsync("1 MOCK");
								await sw.WriteLineAsync("1 ProtocolVersion[0.1]");
								await sw.WriteLineAsync("1 ApplicationVersion[1.0.1]");
								await sw.WriteLineAsync("1 HardwareVersion[1.3]");
								break;
							case "status":
								await sw.WriteLineAsync("1 Status[" + CsOption.FormatState(cs.State) + "]");
								break;
						}
					}
					await sw.WriteLineAsync("<END 0 (ok)>");
					break;
				case "set":
					switch (options[0].Name)
					{
						case "GO":
							cs.State = CsState.On;
							await ReplyCommandStationStatusAsync(line);
							break;
						case "STOP":
							cs.State = CsState.Off;
							await ReplyCommandStationStatusAsync(line);
							break;
						default:
							throw new Exception($"CommandStation-'{command}' failed.");
					}
					break;
				case "request":
				case "release":
					await ReplyOkAsync(line);
					break;
				default:
					throw new NotImplementedException($"CommandStation-'{command}' not implemented.");
			}
		} // proc HandleCommandStationCommandAsync

		private async Task HandleEngineListCommandAsync(string line, string command, List<CsOption> options)
		{
			switch(command)
			{
				case "queryObjects":
					await sw.WriteLineAsync("<REPLY " + line + ">");
					foreach (var cur in cs.Engines)
						await sw.WriteLineAsync($"{cur.Id} {new CsOption("name", cur.Name)}");
						await sw.WriteLineAsync("<END 0 (ok)>");
					break;
				case "request":
				case "release":
					await ReplyOkAsync(line);
					break;
				default:
					throw new NotImplementedException($"EngineList-'{command}' not implemented.");
			}
		} // func HandleEngineListCommandAsync

		private async Task HandleEngineRequest(string line, string command, List<CsOption> options)
		{
			switch (command)
			{
				case "get":
					break;
				case "set":
					break;
				case "request":
				case "release":
					await ReplyOkAsync(line);
					break;
				default:
					throw new NotImplementedException($"Engine-'{command}' not implemented.");
			}
		} // func HandleEngineRequest

		private async Task ReplyOkAsync(string line)
		{
			await sw.WriteLineAsync("<REPLY " + line + ">");
			await sw.WriteLineAsync("<END 0 (ok)>");
		} // func ReplyOkAsync

		private async Task ReplyCommandStationStatusAsync(string line)
		{
			await sw.WriteLineAsync("<REPLY " + line + ">");
			await sw.WriteLineAsync("1 Status[" + CsOption.FormatState(cs.State) + "]");
			await sw.WriteLineAsync("<END 0 (ok)>");
		} // func ReplyCommandStationStatusAsync

		public async Task NotifyEventAsync(int id, CsOption[] options)
		{
			try
			{
				await sw.WriteLineAsync($"<EVENT {id}>");
				foreach (var cur in options)
					await sw.WriteLineAsync($"{id} {cur.ToString()}");
				await sw.WriteLineAsync("<EVENT 0 (ok)>");
			}
			catch (SocketException e)
			{
				Out(e.ToString());
			}
		} // proc NotifyEventAsync

		public void Out(string message)
			=> Debug.Print(Name + ": " + message);

		public string Name => ((IPEndPoint)socket.RemoteEndPoint).Address.ToString();
	} // class ClientEndPoint

	internal sealed class CommandStationMock : INotifyPropertyChanged
	{
		public event PropertyChangedEventHandler PropertyChanged;

		private CsState commandStationState = CsState.On;
		private ObservableCollection<EngineMock> engines = new ObservableCollection<EngineMock>();
		private List<ClientEndPoint> clients = new List<ClientEndPoint>();

		private Socket socketAccept;
		private Task socketAcceptTask;

		public CommandStationMock()
		{
			engines.Add(new EngineMock(this, 1000, "BR235", 0));
			engines.Add(new EngineMock(this, 1001, "DR001", 0));
			engines.Add(new EngineMock(this, 1002, "Talent", 0));

			StartLocalServer(new IPEndPoint(IPAddress.Loopback, 15471));
		} // ctor

		public void NotifyEvent(int id, params CsOption[] options)
		{
			Debug.Print("Notify: {0}: {1}", id, CsOption.Format(options));
		} // proc NotifyEvent

		private void StartLocalServer(IPEndPoint bindPoint)
		{
			socketAccept = new Socket(bindPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

			socketAccept.Bind(bindPoint);
			socketAccept.Listen(10);

			socketAcceptTask = Task.Run(() => AcceptLoop());
		} // proc StartLocalServer

		private void AcceptLoop()
		{
			while (true)
			{
				var s = socketAccept.Accept();
				lock (clients)
					clients.Add(new ClientEndPoint(this, s));
			}
		} // proc AcceptLoop

		public void RemoveClient(ClientEndPoint client)
		{
			lock (clients)
				clients.Remove(client);
		} // proc RemoveClient

		private void OnPropertyChanged(string propertyName)
			=> PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
		
		public CsState State
		{
			get { return commandStationState; }
			set
			{
				if (commandStationState != value)
				{
					commandStationState = value;
					NotifyEvent(1, new CsOption("Status", CsOption.FormatState(commandStationState)));
					OnPropertyChanged(nameof(State));
				}
			}
		} // prop State

		public ObservableCollection<EngineMock> Engines => engines;
	} // class CommandStationMock
}
