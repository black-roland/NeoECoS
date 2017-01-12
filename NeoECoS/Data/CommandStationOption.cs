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
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NeoECoS.Data
{
	#region -- enum CsState -------------------------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary></summary>
	public enum CsState
	{
		None,
		Connecting,
		On,
		Off,
		Shutdown
	} // enum CsState

	#endregion

	#region -- class CsException --------------------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary></summary>
	public class CsException : Exception
	{
		private readonly int returnCode;

		public CsException(int returnCode, string message)
			: base(message)
		{
			this.returnCode = returnCode;
		} // ctor

		public int ReturnCode => returnCode;
	} // class CsException 

	#endregion

	#region -- class CsParseException ---------------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary></summary>
	public class CsParseException : Exception
	{
		private readonly string line;
		private readonly int position;

		public CsParseException(string message, string line, int position)
			: base(message)
		{
			this.line = line;
			this.position = position;
		} // ctor

		public string Line => line;
		public int Position => position;
	} // class CsParseException 

	#endregion

	#region -- class CsUtfString --------------------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary></summary>
	public sealed class CsUtfString
	{
		private readonly string text;

		public CsUtfString(string text)
		{
			this.text = text;
		} // ctor

		public override string ToString()
			=> '"' + text + '"';

		public string Text => text;
	} // class CsUtfString

	#endregion

	#region -- class CsOption -----------------------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary></summary>
	public sealed class CsOption
	{
		private readonly string name;
		private readonly object[] values;

		public CsOption(string name, params object[] values)
		{
			this.name = name;
			this.values = values ?? EmptyValues;
		} // ctor

		public override string ToString()
		{
			var sb = new StringBuilder();
			sb.Append(name);
			if (values.Length > 0)
			{
				sb.Append('[');
				for (var i = 0; i < values.Length; i++)
				{
					if (i > 0)
						sb.Append(",");
					sb.Append(Convert.ToString(values[i], CultureInfo.InvariantCulture));
				}
				sb.Append(']');
			}
			return sb.ToString();
		} // func ToString

		public object GetOptionCore(int index)
			=> index >= 0 && index < values.Length ? values[index] : null;

		public T GetOption<T>(int index)
		{
			var v = GetOptionCore(index);

			// unpack utf string
			var s = v as CsUtfString;
			if (s != null)
				v = s.Text;

			return (T)Convert.ChangeType(v, typeof(T));
		} // func GetOption

		public void WriteOption(StringBuilder sb)
		{
			sb.Append(name);

			if (values.Length > 0)
			{
				sb.Append('[');

				for (var i = 0; i < values.Length; i++)
				{
					if (i > 0)
						sb.Append(',');

					if (values[i] is CsUtfString)
						WriteStringValue(sb, ((CsUtfString)values[i]).Text);
					else
						WriteOtherValue(sb, values[i]);
				}

				sb.Append(']');
			}
		} // proc WriteOption 

		private void WriteStringValue(StringBuilder sb, string text)
		{
			sb.Append('"');
			for (var i = 0; i < text.Length; i++)
			{
				switch (text[i])
				{
					case '"':
						sb.Append("\"\"");
						break;
					case '\n':
						sb.Append(' ');
						break;
					case '\r':
						break;
					default:
						sb.Append(text[i]);
						break;
				}
			}
			sb.Append('"');
		} // func WriteStringValue

		private void WriteOtherValue(StringBuilder sb, object value)
		{
			if (value != null)
			{
				var text = value.ToString();
				if (text.Contains(' ') || text.Contains('\r'))
					throw new ArgumentException("Invalid option");
				sb.Append(text);
			}
		} // proc WriteOtherValue

		public string Name => name;
		public object[] Values => values;

		private static string[] EmptyValues { get; } = new string[0];

		public static void SkipWhiteSpaces(string currentLine, ref int i)
		{
			while (i < currentLine.Length)
			{
				if (!Char.IsWhiteSpace(currentLine[i]))
					break;
				i++;
			}
		} // proc SkipWhiteSpaces

		public static object ParseValue(string currentLine, ref int i)
		{
			SkipWhiteSpaces(currentLine, ref i);

			if (i < currentLine.Length && currentLine[i] == '"') // parse string
			{
				i++;
				var startAt = i;
				var inQuote = false;
				var sb = new StringBuilder();
				while (i < currentLine.Length)
				{
					if (inQuote)
					{
						if (currentLine[i] == '"')
						{
							inQuote = false;
							sb.Append('"');
						}
						else
							break;
					}
					else
					{
						if (currentLine[i] == '"')
							inQuote = true;
						else
							sb.Append(currentLine[i]);
						i++;
					}
				}
				return new CsUtfString(sb.ToString());
			}
			else // parse number or text
			{
				var startAt = i;
				while (i < currentLine.Length)
				{
					var c = currentLine[i];
					if (c == ',' || c == '[' || c == ']' || c == ')' || Char.IsWhiteSpace(c))
						break;
					i++;
				}

				// try get the value
				var value = currentLine.Substring(startAt, i - startAt);
				int t;
				return Int32.TryParse(value, out t) ? (object)t : value;
			}
		} // func ParseValue

		public static CsOption Parse(string currentLine, ref int i)
		{
			// option[arg,arg]

			// read the "name"
			var name = ParseValue(currentLine, ref i);
			if (!(name is string))
				throw new CsParseException("Option line parsing failed (name missing).", currentLine, i);

			// check for values
			var values = new List<object>();
			SkipWhiteSpaces(currentLine, ref i);
			if (i < currentLine.Length && currentLine[i] == '[')
			{
				i++;
				while (i < currentLine.Length)
				{
					var v = ParseValue(currentLine, ref i);
					if (v != null)
						values.Add(v);
					SkipWhiteSpaces(currentLine, ref i);
					if (currentLine[i] == ']')
						break;
					else if (currentLine[i] != ',')
						throw new CsParseException("Option line parsing failed (option value expected).", currentLine, i);
					i++;
				}
				i++;
			}

			return new CsOption((string)name, values.ToArray());
		} // func Parse

		public static string Format(params CsOption[] options)
			=> String.Join(";", from o in options select o.ToString());

		public static CsState ParseState(string state)
		{
			if (String.Compare(state, "STOP", StringComparison.OrdinalIgnoreCase) == 0)
				return CsState.Off;
			else if (String.Compare(state, "GO", StringComparison.OrdinalIgnoreCase) == 0)
				return CsState.On;
			else if (String.Compare(state, "SHUTDOWN", StringComparison.OrdinalIgnoreCase) == 0)
				return CsState.Shutdown;
			else
				return CsState.None;
		} // func ParseState

		public static string FormatState(CsState state)
		{
			switch (state)
			{
				case CsState.Off:
					return "STOP";
				case CsState.On:
					return "GO";
				case CsState.Shutdown:
					return "SHUTDOWN";
				default:
					return null;
			}
		} // func FormatState
	} // class CsOption

	#endregion

	#region -- class CsOptionResult -----------------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary></summary>
	public sealed class CsOptionResult
	{
		private readonly int id;
		private readonly CsOption[] options;

		public CsOptionResult(int id, CsOption[] options)
		{
			this.id = id;
			this.options = options;
		} // ctor

		public override string ToString()
			=> id + " " + String.Join(" ", from o in options select o.ToString());

		public T GetOption<T>(string optionName, T defaultValue)
		{
			var o = options.FirstOrDefault(c => String.Compare(c.Name, optionName, StringComparison.OrdinalIgnoreCase) == 0);
			if (o == null)
				return defaultValue;
			else
			{
				try
				{
					return o.GetOption<T>(0);
				}
				catch
				{
					return defaultValue;
				}
			}
		} // func GetOption

		public int Id => id;
		public CsOption[] Options => options;
	} // class CsOptionResult

	#endregion
}
