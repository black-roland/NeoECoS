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
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using System.Windows.Input;
using NeoECoS.Data;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.UI.Popups;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

namespace NeoECoS
{
	/// <summary>
	/// An empty page that can be used on its own or navigated to within a Frame.
	/// </summary>
	public sealed partial class MainPage : Page
	{
		private CommandStationController commandStation;

		public MainPage()
		{
			this.InitializeComponent();

			commandStation = new CommandStationController(Dispatcher);
			commandStation.NotifyException +=
				async (sender, e) =>
				{
					var msg = new MessageDialog(e.ToString(), "Kommunikationsfehler");
					await msg.ShowAsync();
				};
			this.DataContext = commandStation;

			engineView.Source = commandStation.Engines;
			engineView.View.CurrentChanged += async (object sender, object e) => await commandStation.SetActiveEngineAsync(cbbEngines.SelectedItem as CsEngineObject, true);
		} // ctor

		private async void cmdConnect_Click(object sender, RoutedEventArgs e)
		{
			await commandStation.ConnectAsync(txtHost.Text);
			// await commandStation.ConnectAsync("127.0.0.1");
		} // event cmdConnect_Click

		private async void cmdToggleState_Click(object sender, RoutedEventArgs e)
		{
			switch (commandStation.State)
			{
				case CsState.On:
					await commandStation.StopAsync();
					break;
				case CsState.Off:
					await commandStation.StartAsync();
					break;
			}
		} // event cmdToggleState_Click
	} // class MainPage

	#region -- class MainPageStateButtonConverter ---------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary></summary>
	public sealed class MainPageStateConverter : IValueConverter
	{
		public object Convert(object value, Type targetType, object parameter, string language)
		{
			var state = (CsState)value;

			switch (Type)
			{
				case 1: // Content
					switch (state)
					{
						case CsState.Connecting:
							return "Verbinde";
						case CsState.Off:
							return "Go";
						case CsState.On:
							return "Stop";
						case CsState.Shutdown:
							return "Shutdown";
						default:
							return "None";
					}
				case 2: // IsEnabled
					switch (state)
					{
						case CsState.Off:
						case CsState.On:
							return true;
						default:
							return false;
					}
				case 3: // Text
					switch (state)
					{
						case CsState.Connecting:
							return "Verbindung wird erstellt.";
						case CsState.Off:
							return "System ist im Haltmodus.";
						case CsState.Shutdown:
							return "Verbindung ist getrennt.";
						default:
							return null;
					}
				case 4: // Visibility
					switch (state)
					{
						case CsState.Off:
						case CsState.On:
							return Visibility.Visible;
						default:
							return Visibility.Collapsed;
					}
				default:
					return null;
			}
		} // func Convert

		public object ConvertBack(object value, Type targetType, object parameter, string language) { throw new NotSupportedException(); }

		public int Type { get; set; }
	} // class MainPageStateButtonConverter

	#endregion

	#region -- class NullVisibilityConverter --------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary></summary>
	public sealed class NullVisibilityConverter : IValueConverter
	{
		public object Convert(object value, Type targetType, object parameter, string language)
			=> value == null ? Visibility.Collapsed : Visibility.Visible;

		public object ConvertBack(object value, Type targetType, object parameter, string language)		{			throw new NotSupportedException();		}
	} // class NullVisibilityConverter

	#endregion

	#region -- class SpeedConverter -----------------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary></summary>
	public sealed class SpeedConverter : IValueConverter
	{
		public object Convert(object value, Type targetType, object parameter, string language)
		{
			return System.Convert.ToDouble(value);
		} // func Convert

		public object ConvertBack(object value, Type targetType, object parameter, string language)
		{
			var doubleNum =  System.Convert.ToDouble(value ?? 0.0);

			if (doubleNum < 0)
				doubleNum = 0.0;
			else if (doubleNum > 127)
				doubleNum = 127.0;

			return System.Convert.ToByte(value);
		} // func ConvertBack
	} // class NullVisibilityConverter

	#endregion

	#region -- class SpeedConverter -----------------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary></summary>
	public sealed class BooleanVisibilityConverter : IValueConverter
	{
		public object Convert(object value, Type targetType, object parameter, string language)
			=> System.Convert.ToBoolean(value) ? Visibility.Visible : Visibility.Collapsed;

		public object ConvertBack(object value, Type targetType, object parameter, string language)
			=> (Visibility)value == Visibility.Visible;
	} // class BooleanVisibilityConverter

	#endregion
}
