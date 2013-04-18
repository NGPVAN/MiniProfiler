namespace StackExchange.Profiling.SqlFormatters
{
    using System;
    using System.Collections.Generic;
    using System.Data;
    using System.Globalization;
    using System.Linq;
    using System.Text;
    using System.Runtime.Serialization.Json;
    using System.IO;

    /// <summary>
    /// Formats SQL server queries with a DECLARE up top for parameter values
    /// </summary>
    public class SqlServerFormatter : ISqlFormatter
    {
        /// <summary>
        /// The parameter translator.
        /// </summary>
        private static readonly Dictionary<DbType, Func<SqlTimingParameter, string>> ParamTranslator;

        /// <summary>
        /// don't quote.
        /// </summary>
        private static readonly string[] DontQuote = new[] { "Int16", "Int32", "Int64", "Boolean", "Byte[]" };

        /// <summary>
        /// get the 'with length' formatter.
        /// </summary>
        /// <param name="native">The native string.</param>
        /// <returns>the SQL timing parameter formatter function</returns>
        private static Func<SqlTimingParameter, string> GetWithLenFormatter(string native)
        {
            var capture = native;
            return p =>
            {
                if (p.Size < 1)
                {
                    return capture;
                }
                return capture + "(" + (p.Size > 8000 ? "max" : p.Size.ToString(CultureInfo.InvariantCulture)) + ")";
            };
        }

        /// <summary>
        /// Initialises static members of the <see cref="SqlServerFormatter"/> class.
        /// </summary>
        static SqlServerFormatter()
        {
            ParamTranslator = new Dictionary<DbType, Func<SqlTimingParameter, string>>
            {
                { DbType.AnsiString, GetWithLenFormatter("varchar") },
                { DbType.String, GetWithLenFormatter("nvarchar") },
                { DbType.AnsiStringFixedLength, GetWithLenFormatter("char") },
                { DbType.StringFixedLength, GetWithLenFormatter("nchar") },
                { DbType.Byte, p => "tinyint" },
                { DbType.Int16, p => "smallint" },
                { DbType.Int32, p => "int" },
                { DbType.Int64, p => "bigint" },
                { DbType.DateTime, p => "datetime" },
                { DbType.Guid, p => "uniqueidentifier" },
                { DbType.Boolean, p => "bit" },
                { DbType.Binary, GetWithLenFormatter("varbinary") },
            };

        }

        /// <summary>
        /// Formats the SQL in a SQL-Server friendly way, with DECLARE statements for the parameters up top.
        /// </summary>
        /// <param name="timing">The <c>SqlTiming</c> to format</param>
        /// <returns>A formatted SQL string</returns>
        public string FormatSql(SqlTiming timing)
        {
            if (timing.Parameters == null || timing.Parameters.Count == 0)
            {
                return timing.CommandString;
            }

            var buffer = new StringBuilder();

            foreach (var p in timing.Parameters)
            {

                string paramFormat = "DECLARE {0} {1} = {2};";

                DbType parsed;
                string resolvedType = null;
                if (!Enum.TryParse(p.DbType, out parsed))
                {
                    resolvedType = p.DbType;
                }

                string val = p.Value;

                if (resolvedType == null)
                {
                    Func<SqlTimingParameter, string> translator;
                    if (ParamTranslator.TryGetValue(parsed, out translator))
                    {
                        resolvedType = translator(p);
                    }
                    resolvedType = resolvedType ?? p.DbType;
                    val = PrepareValue(p);
                }
                else
                {
                    paramFormat = "DECLARE {0} {1};";
                    val = GetJsonVal(p);
                    if (val != null && val.Length > 0)
                    {
                        paramFormat += "\nINSERT INTO {0} VALUES {2};";
                    }
                }

                var niceName = p.Name;
                if (!niceName.StartsWith("@"))
                {
                    niceName = "@" + niceName;
                }


                buffer.AppendFormat(paramFormat, niceName, resolvedType, val);
                buffer.AppendLine();
            }

            return buffer
                .AppendLine()
                .AppendLine()
                .Append(timing.CommandString)
                .ToString();
        }

        /// <summary>
        /// prepare the value.
        /// </summary>
        /// <param name="parameter">The parameter.</param>
        /// <returns>the prepared parameter value.</returns>
        private string PrepareValue(SqlTimingParameter parameter)
        {

            if (parameter.Value == null)
            {
                return "null";
            }

            if (DontQuote.Contains(parameter.DbType))
            {
                if (parameter.DbType == "Boolean")
                {
                    return parameter.Value == "True" ? "1" : "0";
                }

                return parameter.Value;
            }

            var prefix = string.Empty;
            if (parameter.DbType == "String" || parameter.DbType == "StringFixedLength")
            {
                prefix = "N";
            }

            return prefix + "'" + parameter.Value.Replace("'", "''") + "'";
        }

        private string GetJsonVal(SqlTimingParameter p)
        {
            if (p.Value == null || p.Value.Length == 0)
            {
                return null;
            }

            DataContractJsonSerializer ser = new DataContractJsonSerializer(typeof(List<List<string>>));


            using (Stream s = GenerateStreamFromString(p.Value))
            {
                var data = (List<List<String>>)ser.ReadObject(s);

                var rows = new List<string>();

                foreach (List<string> row in data)
                {
                    for (int i = 0; i < row.Count; i++)
                    {
                        row[i] = "'" + row[i].Replace("'", "''") + "'";
                    }
                    rows.Add("(" + string.Join(",", row) + ")");
                }

                return string.Join(",\n", rows);

            }

        }

        private Stream GenerateStreamFromString(string s)
        {
            MemoryStream stream = new MemoryStream();
            StreamWriter writer = new StreamWriter(stream);
            writer.Write(s);
            writer.Flush();
            stream.Position = 0;
            return stream;
        }

    }
}
