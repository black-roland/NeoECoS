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
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Windows.UI.Core;

namespace NeoECoS.Data
{
	// Cs = Command Station

	#region -- interface ICsView --------------------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary></summary>
	public interface ICsView : IDisposable
	{
		void OnEvent(CsOption[] options);

		int Id { get; }
	} // interface ICsEventSink

	#endregion

	#region -- class CsEngineObject -----------------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary>Engine definition.</summary>
	public sealed class CsEngineObject
	{
		private readonly CommandStationController cs;
		private readonly int id;
		private readonly string name;
		private readonly int addr;

		public CsEngineObject(CommandStationController cs, int id, string name, int addr)
		{
			this.cs = cs;
			this.id = id;
			this.name = name;
			this.addr = addr;
		} // ctor
		
		public CsEngineView CreateView()
			=> new CsEngineView(cs, this);

		public int Id => id;
		public string Name => name;
		public int Address => addr;
	} // class CsEngineObject

	#endregion

	#region -- class CsFunctionState ----------------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary></summary>
	public sealed class CsFunctionState : ICommand, INotifyPropertyChanged
	{
		public event EventHandler CanExecuteChanged;
		public event PropertyChangedEventHandler PropertyChanged;

		private readonly CsEngineView engineView;
		private readonly byte index;

		private bool isActive = false;
		private int description = -1;

		public CsFunctionState(CsEngineView engineView, byte index)
		{
			this.engineView = engineView;
			this.index = index;
		} // ctor

		public bool CanExecute(object parameter)
			=> IsEnabled;

		public void Execute(object parameter)
		{
			if (IsEnabled)
				IsActive = !isActive;
		} // proc Execute

		public void Refresh()
			=> Task.Run(() => engineView.Controller.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => CanExecuteChanged?.Invoke(this, EventArgs.Empty)));

		public void SetActiveCore(bool newActive)
		{
			if (newActive != isActive)
			{
				isActive = newActive;
				OnPropertyChanged(nameof(IsActive));
			}
		} // prop SetActive

		public void SetDescriptionCore(int newDescription)
		{
			if (newDescription != description)
			{
				description = newDescription;
				OnPropertyChanged(nameof(IsEnabled));
				OnPropertyChanged(nameof(Description));
			}
		} // proc SetDescriptionCore

		public void OnPropertyChanged(string propertyName)
			=> Task.Run(() => engineView.Controller.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName))));

		public byte Index => index;

		public bool IsEnabled => description >= 0;

		public bool IsActive
		{
			get { return isActive; }
			set
			{
				if (value != isActive)
					Task.Run(async () => await engineView.SetFunctionAsync(index, value));
			}
		} // prop Active

		public int Description=> description;
	} // class CsFunctionState

	#endregion

	#region -- class CsEngineView -------------------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary></summary>
	public sealed class CsEngineView : INotifyPropertyChanged, INotifyCollectionChanged, IEnumerable<CsFunctionState>, ICsView, IDisposable
	{
		private const string SpeedCommand = "speed";
		private const string DirectionCommand = "dir";
		private const string ControlCommand = "control";

		private const string FunctionStateCommand = "func";
		private const string FunctionExistsCommand = "funcexists";

		#region -- class SimpleCommand ----------------------------------------------------

		private sealed class SimpleCommand : ICommand
		{
			public event EventHandler CanExecuteChanged;

			private readonly Action execute;
			private readonly Func<bool> canExecute;

			public SimpleCommand(Action execute, Func<bool> canExecute)
			{
				this.execute = execute;
				this.canExecute = canExecute;
			} // ctor

			public void Refresh()
				=> CanExecuteChanged?.Invoke(this, EventArgs.Empty);

			public bool CanExecute(object parameter)
				=> canExecute?.Invoke() ?? true;

			public void Execute(object parameter)
				=> execute?.Invoke();
		} // class SimpleCommand

		#endregion

		public event PropertyChangedEventHandler PropertyChanged;
		public event NotifyCollectionChangedEventHandler CollectionChanged;

		private readonly CommandStationController cs;
		private readonly CsEngineObject engine;

		private bool hasControl = false;
		private byte speed = 0;
		private byte direction = 0;
		private readonly CsFunctionState[] functions;


		private readonly SimpleCommand forwardCommand;
		private readonly SimpleCommand backwardCommand;
		private readonly SimpleCommand stopCommand;

		public CsEngineView(CommandStationController cs, CsEngineObject engine)
		{
			this.cs = cs;
			this.engine = engine;
			this.functions = new CsFunctionState[29];
			for (var i = 0; i < functions.Length; i++)
				functions[i] = new CsFunctionState(this, (byte)i);

			this.forwardCommand = new SimpleCommand(() => Direction = 0, () => Direction != 0);
			this.backwardCommand = new SimpleCommand(() => Direction = 1, () => Direction != 1);
			this.stopCommand = new SimpleCommand(async () => await StopAsync(), () => Speed > 0);
		} // ctor

		public async Task InitAsync()
		{
			await cs.RegisterViewAsync(this);

			// get current state
			UpdateProperties((await cs.SendCommandAsync("get", Id, new CsOption(SpeedCommand), new CsOption(DirectionCommand))).FirstOrDefault());

			// function set
			var options = new CsOption[functions.Length * 2];
			
			for (var i = 0; i < functions.Length; i++)
			{
				options[i] = new CsOption(FunctionExistsCommand, i);
				options[i + functions.Length] = new CsOption(FunctionStateCommand, i);
			}
			UpdateProperties((await cs.SendCommandAsync("get", Id, options)).FirstOrDefault());

			OnCollectionChanged();
		} // func InitAsync

		public async Task DisposeAsync()
		{
			await cs.UnregisterViewAsync(this);
		} // proc DisposeAsync

		public void Dispose()
		{
			DisposeAsync().Wait();
		} // proc Dispose

		private void UpdateProperties(CsOptionResult r)
		{
			foreach (var c in r.Options)
			{
				switch (c.Name)
				{
					case SpeedCommand:
						SetProperty(ref speed, c.GetOption<byte>(0), nameof(Speed));
						break;
					case DirectionCommand:
						SetProperty(ref direction, c.GetOption<byte>(0), nameof(Direction));
						break;
					case ControlCommand:
						// if (c.GetOption<string>(0) != "other")
							SetProperty(ref hasControl, false, nameof(HasControl));
						break;
					case FunctionExistsCommand:
						{
							var idx = c.GetOption<int>(0);
							if (idx >= 0 && idx < functions.Length)
							functions[idx].SetDescriptionCore(c.GetOption<int>(1));
						}
						break;
					case FunctionStateCommand:
						{
							var idx = c.GetOption<int>(0);
							if (idx >= 0 && idx < functions.Length)
								functions[idx].SetActiveCore(c.GetOption<int>(1) != 0);
						}
						break;
				}
			}
		} // proc UpdateProperties

		public async Task <bool> SetControlAsync(bool request, bool force=false)
		{
			await cs.SendCommandAsync(request ? "request" : "release", Id, new CsOption(ControlCommand));
			SetProperty(ref hasControl, false, nameof(HasControl));
			return request;
		} // func SetControlAsync

		public  async Task SetFunctionAsync(byte index, bool newActive)
		{
			await cs.SendCommandAsync("set", Id, new CsOption("func", index, newActive ? 1 : 0));
			functions[index].SetActiveCore(newActive);
		} // proc SetActiveAsync

		public async Task StopAsync()
		{
			await cs.SendCommandAsync("set", Id, new CsOption("stop"));
			SetProperty<byte>(ref speed, 0, nameof(Speed));
		} // proc StopAsync

		public IEnumerator<CsFunctionState> GetEnumerator()
		{
			foreach (var c in functions)
				if (c.IsEnabled)
					yield return c;
		} // func GetEnumerator

		IEnumerator IEnumerable.GetEnumerator()
			=> GetEnumerator();

		public void OnPropertyChanged(string propertyName)
			=> Task.Run(() => cs.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => OnPropertyChangedUI(propertyName)));

		private void OnPropertyChangedUI(string propertyName)
		{
			switch (propertyName)
			{
				case nameof(Direction):
					forwardCommand.Refresh();
					backwardCommand.Refresh();
					break;
				case nameof(Speed):
					stopCommand.Refresh();
					break;
			}
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
		} // proc OnPropertyChangedUI

		public void OnCollectionChanged()
			=> Task.Run(() => cs.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset))));

		private void SetProperty<T>(Action<T> setVar, T currentValue, T value, string sendCommand, string propertyName)
		{
			if (!Object.Equals(currentValue, value))
			{
				if (sendCommand != null)
				{
					Task.Run(async () =>
						{
							await cs.SendCommandAsync("set", Id, new CsOption(sendCommand, value));
							SetProperty(setVar, currentValue, value, null, propertyName);
						}
					);
				}
				else
				{
					setVar(value);
					OnPropertyChanged(propertyName);
				}
			}
		} // proc SetProperty

		private void SetProperty<T>(ref T var, T value, string propertyName)
		{
			if (!Object.Equals(var, value))
			{
				var = value;
				OnPropertyChanged(propertyName);
			}
		} // proc SetProperty

		void ICsView.OnEvent(CsOption[] options)
			=> UpdateProperties(new CsOptionResult(Id, options));

		public int Id => engine.Id;

		public CsEngineObject Engine => engine;
		public CommandStationController Controller => cs;

		public byte Speed
		{
			get { return speed; }
			set { SetProperty(v => speed = v, speed, value, SpeedCommand, nameof(Speed)); }
		} // prop Speed

		public byte Direction
		{
			get { return direction; }
			set { SetProperty(v => direction = v, direction, value, DirectionCommand, nameof(Direction)); }
		} // prop Speed

		public bool HasControl => hasControl;

		public ICommand ForwardCommand => forwardCommand;
		public ICommand BackwardCommand => backwardCommand;
		public ICommand StopCommand => stopCommand;
	} // class EngineView

	#endregion

	#region -- class CsEngineCollection -------------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary></summary>
	public sealed class CsEngineCollection : ObservableCollection<CsEngineObject>, ICsView
	{
		private readonly CommandStationController controller;

		public CsEngineCollection(CommandStationController controller)
		{
			this.controller = controller;

			controller.RegisterViewAsync(this).Wait();
		} // ctor

		public void Dispose()
		{
			Dispose(true);
		} // proc Dispose

		private void Dispose(bool disposing)
		{
			if (disposing)
				controller.UnregisterViewAsync(this).Wait();
		} // proc Dispose

		void ICsView.OnEvent(CsOption[] options)
		{
			// todo:
		}

		int ICsView.Id => 10;
	} // class CsEngineCollection

	#endregion

	#region -- class CsNotifyExceptionArgs ----------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary></summary>
	public class CsNotifyExceptionArgs : EventArgs
	{
		private readonly Exception ex;

		public CsNotifyExceptionArgs(Exception ex)
		{
			this.ex = ex;
		} // ctor

		public Exception Exception => ex;
	} // class CsNotifyExceptionArgs

	#endregion

	#region -- class CommandStationController -------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary></summary>
	public sealed class CommandStationController : INotifyPropertyChanged, ICsView, IDisposable
	{
		#region -- class ReplyWaitItem ----------------------------------------------------

		///////////////////////////////////////////////////////////////////////////////
		/// <summary></summary>
		private class ReplyWaitItem
		{
			private readonly string commandLine;
			private readonly TaskCompletionSource<CsOptionResult[]> task;
			private int refCounter = 0;

			public ReplyWaitItem(string commandLine)
			{
				this.commandLine = commandLine;
				this.task = new TaskCompletionSource<CsOptionResult[]>();
				AddRef();
			} // ctor

			public void AddRef()
			{
				refCounter++;
			} // proc AddRef

			public bool Release()
				=> --refCounter <= 0;

			public string CommandLine => commandLine;
			public TaskCompletionSource<CsOptionResult[]> Task => task;
		} // class ReplyWaitItem

		#endregion

		public event PropertyChangedEventHandler PropertyChanged;
		public event EventHandler<CsNotifyExceptionArgs> NotifyException;

		private readonly CoreDispatcher dispatcher;
		private CsState state;

		private string commandStationControllerName = String.Empty;
		private string protocolVersion = String.Empty;
		private string applicationVersion = String.Empty;
		private string hardwareVersion  = String.Empty;

		private CsEngineCollection engines;
		private CsEngineView activeEngine = null;

		private Socket socket = null;
		private readonly List<ReplyWaitItem> replyWaiter = new List<ReplyWaitItem>(); // Liste mit reply Anforderungen
		private readonly List<ICsView> registeredViews = new List<ICsView>();         // Registrierte Views

		#region -- Ctor/Dtor --------------------------------------------------------------

		public CommandStationController(CoreDispatcher dispatcher)
		{
			this.dispatcher = dispatcher;
			this.state = CsState.None;
			this.parseBuffer = new ParseLineBuffer(this, true);
			this.engines = new CsEngineCollection(this);

			RegisterViewAsync(this).Wait();
		} // ctor

		public void Dispose()
		{
			Dispose(true);
		} // proc Dispose

		private void Dispose(bool disposing)
		{
			if (disposing)
			{
				UnregisterViewAsync(this).Wait();
				CloseAsync().Wait();
			}
		} // proc Dispose

		#endregion

		#region -- Connect ----------------------------------------------------------------

		public async Task<bool> ConnectAsync(string target, int port = 15471)
		{
			// close current connection
			await CloseAsync();

			EndPoint ep;
			IPAddress addr;
			if (IPAddress.TryParse(target, out addr))
				ep = new IPEndPoint(addr, port);
			else
				ep = new DnsEndPoint(target, port);

			socket = new Socket(addr.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
			var result = new TaskCompletionSource<bool>();

			var e = new SocketAsyncEventArgs();
			e.UserToken = result;
			e.RemoteEndPoint = ep;
			e.Completed += EndConnectAsync;

			State = CsState.Connecting;
			if (!socket.ConnectAsync(e))
				EndConnectAsync(socket, e);

			return await result.Task;
		} // proc ConnectAsync

		private void EndConnectAsync(object sender, SocketAsyncEventArgs e)
		{
			var result = (TaskCompletionSource<bool>)e.UserToken;

			if (e.SocketError == SocketError.Success)
			{
				OnPropertyChanged(nameof(EndPoint));

				// Start reader
				BeginReadSocketAsync();

				// Hole infos ab
				BeginRefresh();

				result.SetResult(true);
			}
			else
			{
				((Socket)sender).Dispose();
				socket = null;
				result.SetException(new SocketException((int)e.SocketError));
			}
		} // proc EndConnectAsync

		public async Task CloseAsync()
		{
			if (socket != null)
			{
				await SetActiveEngineAsync(null, false);
				await dispatcher.RunAsync(CoreDispatcherPriority.Normal, engines.Clear);

				State = CsState.Shutdown;
				socket.Dispose();
				socket = null;
			}
		} // proc Close

		#endregion

		#region -- Receive Data -----------------------------------------------------------

		private void BeginReadSocketAsync()
		{
			var e = new SocketAsyncEventArgs();
			e.SetBuffer(new byte[1024], 0, 1024);
			e.Completed += EndReadSocketAsync;
			ReadSocketAsync(e);
		} // func BeginReadSocketAsync

		private void ReadSocketAsync(SocketAsyncEventArgs e)
		{
			if (!socket.ReceiveAsync(e))
				EndReadSocketAsync(socket, e);
		} // proc ReadSocketAsync

		private void EndReadSocketAsync(object sender, SocketAsyncEventArgs e)
		{
			try
			{
				if (e.SocketError == SocketError.Success)
				{
					try
					{
						ParseBuffer(Encoding.UTF8.GetString(e.Buffer, 0, e.BytesTransferred)); // decode data
					}
					catch (CsParseException ex)
					{
						OnNotifyException(ex);
					}
					ReadSocketAsync(e); // read next block
				}
				else
					throw new SocketException((int)e.SocketError);
			}
			catch (Exception ex)
			{
				OnNotifyException(ex);
				Task.Run(() => CloseAsync());
			}
		} // proc EndReadSocketAsync

		#region -- class ParseLineBuffer --------------------------------------------------

		///////////////////////////////////////////////////////////////////////////////
		/// <summary></summary>
		private sealed class ParseLineBuffer
		{
			private readonly CommandStationController controller;
			private readonly bool emitLines;
			private string currentMessageHeader = String.Empty;
			private readonly List<CsOptionResult> currentResults = new List<CsOptionResult>();
			private int currentResultId = -1;
			private readonly List<CsOption> currentOptions = new List<CsOption>();

			private bool endReaded = false;
			private Exception errorState = null;

			public ParseLineBuffer(CommandStationController controller, bool emitLines)
			{
				this.controller = controller;
				this.emitLines = emitLines;
			} // ctor

			private void Clear()
			{
				currentMessageHeader = String.Empty;
				currentResults.Clear();
				currentResultId = -1;
				currentOptions.Clear();
				errorState = null;
				endReaded = false;
			} // proc Clear

			private void SetAnswerForReply(int returnCode, string message)
			{
				int eventId;
				if (Int32.TryParse(currentMessageHeader, out eventId))
				{
					if (errorState != null) // notify exception
						controller.OnNotifyException(errorState);
					else if (returnCode == 0) // valid event
					{
						AddToResults(-1);
						controller.ProcessEvent(currentResults.ToArray());
					}
					// else ignore error code
				}
				else
				{
					if (errorState != null) // reply exception
					{
						if (String.IsNullOrEmpty(currentMessageHeader))
							controller.OnNotifyException(errorState);
						else
							controller.SetAnswerForReply(currentMessageHeader, t => t.SetException(errorState));
					}
					else if (returnCode != 0) // reply error
					{
						controller.SetAnswerForReply(currentMessageHeader, t => t.SetException(new CsException(returnCode, message)));
					}
					else
					{
						AddToResults(-1);
						controller.SetAnswerForReply(currentMessageHeader, t => t.SetResult(currentResults.ToArray()));
					}
				}
			} // proc SetAnswerForReply

			public void ParseLine(string currentLine)
			{
				if (emitLines)
					Debug.WriteLine(currentLine);

				if (currentLine.StartsWith("<REPLY ") || currentLine.StartsWith("<EVENT")) // reply, event
				{
					if (currentMessageHeader != String.Empty)
						throw new CsParseException("Invalid command start.", currentLine, 0);

					// message header
					currentMessageHeader = currentLine.Substring(7, currentLine.Length - 8);
				}
				else if (currentLine.StartsWith("<END")) // end
				{

					endReaded = true;
					if (errorState != null) // parse fehler aufgetreten, ignoriere das End-Tag und gib ihn weiter
					{
						SetAnswerForReply(-1, errorState.Message);
					}
					else // hole den return code ab "<END returncode (message)>"
					{
						var p = 4;
						CsOption.SkipWhiteSpaces(currentLine, ref p);
						var returnCode = (int)CsOption.ParseValue(currentLine, ref p);
						if (returnCode > 0)
						{
							// "<END 11 (jjjjjj)>"
							CsOption.SkipWhiteSpaces(currentLine, ref p);

							if (p >= currentLine.Length)
								throw new CsParseException("Unexpected eof.", currentLine, p);

							if (currentLine[p] == '(' && currentLine.EndsWith(")>"))
								SetAnswerForReply(returnCode, currentLine.Substring(p + 1, currentLine.Length - p - 3));

							throw new CsParseException($"Could not read error message for code '{returnCode}'.", currentLine, 0);
						}
						else
						{
							SetAnswerForReply(0, "ok");
						}
					}

					// clear message state
					Clear();
				}
				else if (errorState == null) // simple option line
				{
					var p = 0;
					var id = (int)CsOption.ParseValue(currentLine, ref p); // parse id
					if (id != currentResultId)
						AddToResults(id);

					// collect the options
					while (p < currentLine.Length)
					{
						CsOption.SkipWhiteSpaces(currentLine, ref p); // skip
						currentOptions.Add(CsOption.Parse(currentLine, ref p));
					}
				}
			} // proc ParseLine

			private void AddToResults(int id)
			{
				if (currentOptions.Count > 0)
				{
					// add the result
					currentResults.Add(new CsOptionResult(currentResultId, currentOptions.ToArray()));
				}

				// result the result
				currentResultId = id;
				currentOptions.Clear();
			} // proc AddToResults

			public void SetException(Exception e)
			{
				errorState = e;
				if (endReaded)
				{
					SetAnswerForReply(-1, e.Message);
					Clear();
				}
			} // proc SetException
		} // class ParseBuffer

		#endregion

		private string lastFragment = String.Empty;
		private readonly ParseLineBuffer parseBuffer;

		private void ParseBuffer(string data)
		{
			var startAt = 0;
			var i = 0;
			while (i < data.Length)
			{
				var c = data[i];
				if (c == '\r' || c == '\n')
				{
					var currentLine = data.Substring(startAt, i - startAt);
					if (lastFragment.Length > 0)
						currentLine = lastFragment + currentLine;
					lastFragment = String.Empty;
					try
					{
						parseBuffer.ParseLine(currentLine);
					}
					catch (Exception e)
					{
						parseBuffer.SetException(e);
					}
					if (c == '\r' && i < data.Length && data[i + 1] == '\n')
						i++;
					startAt = i + 1;
				}
				i++;
			}

			// set the last fragment
			var l = i - startAt;
			lastFragment = l == 0 ? String.Empty : data.Substring(startAt, i - startAt);
		} // proc ParseBuffer

		#endregion

		#region -- Views ------------------------------------------------------------------

		public async Task RegisterViewAsync(ICsView view)
		{
			if (IsConnected)
				await SendCommandAsync("request", view.Id, new CsOption("view"));

			// add the view
			lock (registeredViews)
				registeredViews.Add(view);
		} // proc RegisterView

		public async Task UnregisterViewAsync(ICsView view)
		{
			bool unRegisterView;

			// remove the view
			lock (registeredViews)
			{
				registeredViews.Remove(view);
				unRegisterView = registeredViews.Find(c => c.Id == view.Id) == null;
			}

			// request befehl
			if (unRegisterView && IsConnected)
				await SendCommandAsync("release", view.Id, new CsOption("view"));
		} // proc UnregisterView

		private void ProcessEvent(CsOptionResult[] eventData)
		{
			for (var i = 0; i < eventData.Length; i++)
			{
				var ev = eventData[i];

				lock (registeredViews)
				{
					var view = registeredViews.Find(c => c.Id == ev.Id);
					if (view != null)
					{
						try
						{
							view.OnEvent(ev.Options);
						}
						catch (Exception e)
						{
							OnNotifyException(e);
						}
					}
				}
			}
		} // proc ProcessEvent

		#endregion

		#region -- SendCommandAsync -------------------------------------------------------

		public Task<CsOptionResult[]> SendCommandAsync(string command, int id, params CsOption[] options)
		{
			// test if the socket is connected
			if (!IsConnected)
				throw new InvalidOperationException("No connection.");

			// Build command block
			var sb = new StringBuilder();
			sb.Append(command);
			sb.Append('(');
			sb.Append(id);

			foreach (var o in options)
			{
				sb.Append(',');
				o.WriteOption(sb);
			}
			sb.Append(')');

			// Enque reqeust for answer
			var timeout = new CancellationTokenSource(5000);
			var resultSource = EnqueCommandAnswer(sb.ToString(), timeout.Token);

			// Send the command
			sb.Append('\n');
			var e = new SocketAsyncEventArgs();
			var data = Encoding.UTF8.GetBytes(sb.ToString());
			e.SetBuffer(data, 0, data.Length);
			e.UserToken = resultSource;
			e.Completed += EndSendCommandAsync;
			if (!socket.SendAsync(e))
				EndSendCommandAsync(socket, e);

			return resultSource.Task.Task.ContinueWith(t =>
			{
				try
				{
					return t.Result;
				}
				finally
				{
					timeout.Dispose();
				}
			});
		} // func SendCommandAsync

		private void EndSendCommandAsync(object sender, SocketAsyncEventArgs e)
		{
			if (e.SocketError != SocketError.Success)
			{
				var resultSource = (ReplyWaitItem)e.UserToken;
				RemoveCommandAnswer(resultSource);
				resultSource.Task.SetException(new SocketException((int)e.SocketError));
			}
		} // proc EndSendCommandAsync

		private void SetAnswerForReply(string commandLine, Action<TaskCompletionSource<CsOptionResult[]>> setResult)
		{
			lock (replyWaiter)
			{
				var f = replyWaiter.Find(cur => cur.CommandLine == commandLine);
				if (f != null)
				{
					setResult(f.Task);
					replyWaiter.Remove(f);
				}
			}
		} // func SetAnswerForReply

		private void SetCancelForReply(ReplyWaitItem reply)
		{
			lock (replyWaiter)
			{
				replyWaiter.Remove(reply);
				reply.Task.SetCanceled();
			}
		} // proc SetCancelForReply

		private ReplyWaitItem EnqueCommandAnswer(string commandLine, CancellationToken cancellationToken)
		{
			lock (replyWaiter)
			{
				// füge den waiter hinzu
				var reply = replyWaiter.Find(cur => cur.CommandLine == commandLine);
				if (reply == null)
				{
					reply = new ReplyWaitItem(commandLine);
					replyWaiter.Add(reply);
				}
				else
					reply.AddRef();

				// register abbruch
				cancellationToken.Register(() => SetCancelForReply(reply));

				return reply;
			}
		} // func EnqueCommandAnswer

		private void RemoveCommandAnswer(ReplyWaitItem replyWait)
		{
			lock (replyWaiter)
			{
				if (replyWait.Release())
					replyWaiter.Remove(replyWait);
			}
		} // func RemoveCommandAnswer

		#endregion

		#region -- Refresh ----------------------------------------------------------------

		private void BeginRefresh()
		{
			Task.Run(RefreshAsync).ContinueWith(
				t =>
				{
					if (t.IsFaulted)
						OnNotifyException(t.Exception);
				}
			);
		} // proc BeginRefresh

		public async Task RefreshAsync()
		{
			try
			{
				// get status information
				var r = await SendCommandAsync("get", 1, new CsOption("info"), new CsOption("status"));

				commandStationControllerName = r[0].Options.FirstOrDefault(o => o.Values.Length == 0)?.Name;

				protocolVersion = r[0].GetOption("ProtocolVersion", String.Empty);
				applicationVersion = r[0].GetOption("ApplicationVersion", String.Empty);
				hardwareVersion = r[0].GetOption("HardwareVersion", String.Empty);
				
				OnPropertyChanged(nameof(Name));
				OnPropertyChanged(nameof(ProtocolVersion));
				OnPropertyChanged(nameof(ApplicationVersion));
				OnPropertyChanged(nameof(HardwareVersion));

				UpdateStatus(r);

				// register all views
				Task[] tasks;
				lock (registeredViews)
					tasks = (from v in registeredViews select SendCommandAsync("request", v.Id, new CsOption("view"))).ToArray();
				await Task.WhenAll(tasks);

				// get registered
				r = await SendCommandAsync("queryObjects", 10, new CsOption("name"), new CsOption("addr"));
				await dispatcher.RunAsync(CoreDispatcherPriority.Normal,
					 () =>
					 {
						 engines.Clear();
						 foreach (var c in r)
							 engines.Add(new CsEngineObject(this, c.Id, c.GetOption("name", $"E{c.Id}"), c.GetOption("addr", -1)));
					 });

				// foreach (var engine in engines)
				// {
				// 	var desc = await SendCommandAsync("get", engine.Id, new CsOption("locodesc"));
				// }
			}
			catch (Exception)
			{
				try { await Task.Run(() => CloseAsync()); }
				catch { }
				throw;
			}
			// r = await SendCommandAsync("set", 1002, new CsOption("func", 1, 0));
			// r = await SendCommandAsync("set", 1002, new CsOption("func", 1, 1));
		} // proc Refresh

		#endregion

		#region -- OnNotifyException, OnPropertyChanged, OnEvent --------------------------

		private void OnNotifyException(Exception e)
		{
			Debug.WriteLine(e.ToString());
			Task.Run(() => dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => NotifyException?.Invoke(this, new CsNotifyExceptionArgs(e))));
		} // proc OnNotifyException

		private void OnPropertyChanged(string propertyName)
		{
			Task.Run(() => dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName))));
		} // proc OnPropertyChanged

		void ICsView.OnEvent(CsOption[] options)
		{
			foreach (var o in options)
			{
				if (String.Compare(o.Name, "status", StringComparison.OrdinalIgnoreCase) == 0)
					State = CsOption.ParseState(o.GetOption<string>(0));
			}
		} // proc OnEvent

		#endregion

		#region -- Status-Verwaltung ------------------------------------------------------

		private async Task RefreshStatusAsync()
			=> UpdateStatus(await SendCommandAsync("get", 1, new CsOption("status")));

		private void UpdateStatus(CsOptionResult[] r)
		{
			State = CsOption.ParseState(r[0].GetOption("status", String.Empty));
		} // proc UpdateStatus

		public async Task StartAsync()
		{
			await SendCommandAsync("set", 1, new CsOption("go"));
			await RefreshStatusAsync();
		} // proc StartAsync

		public async Task StopAsync()
		{
			await SendCommandAsync("set", 1, new CsOption("stop"));
			await RefreshStatusAsync();
		} // proc StopAsync

		#endregion

		#region -- Active Engine ----------------------------------------------------------

		public async Task SetActiveEngineAsync(CsEngineObject engine, bool setControl)
		{
			if (activeEngine?.Engine == engine)
				return;

			if (activeEngine != null)
			{
				await activeEngine.DisposeAsync();
				activeEngine = null;
			}

			if (engine != null)
			{
				activeEngine = new CsEngineView(this, engine);
				try
				{
					await activeEngine.InitAsync();
				}
				catch (Exception e)
				{
					OnNotifyException(e);
				}
			}

			OnPropertyChanged(nameof(ActiveEngine));
		} // proc SetActiveEngine		

		#endregion

		int ICsView.Id => 1;

		public string Name => commandStationControllerName;
		public string ProtocolVersion => protocolVersion;
		public string ApplicationVersion => applicationVersion;
		public string HardwareVersion => hardwareVersion;

		public string EndPoint
		{
			get
			{
				if (socket != null)
				{
					var ip = socket.RemoteEndPoint as IPEndPoint;
					if (ip != null)
						return ip.Address.ToString();
					else
						return null;
				}
				else
					return null;
			}
		} // prop EndPoint

		public CsState State
		{
			get { return state; }
			private set
			{
				if (state != value)
				{
					state = value;
					OnPropertyChanged(nameof(State));
				}
			}
		} // prop State

		private bool IsConnected => socket?.Connected ?? false;
		public CoreDispatcher Dispatcher => dispatcher;

		public CsEngineCollection Engines => engines;

		public CsEngineView ActiveEngine => activeEngine;
	} // class CommandStationController

	#endregion
}


/*
 * <REPLY cmd>
 * <EVENT id>
 * id option
 * <END x (str)>
 * 
 * option ::= identifier [ var, var, ... ]
 * var ::= "str" -> escaped ""
 * var ::= zahl
 * 
 * befehl(id, options) -> max 10 options
 */
