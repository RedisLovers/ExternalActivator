using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data.SqlClient;
using System.IO;
using System.Text;
using System.Xml.Linq;
using Sider;

namespace SbeaSample.ConsoleApp
{
    public static class Program
    {
        /// <summary>
        /// Database server for source DB.
        /// </summary>
        private static readonly string _sourceServer = ConfigurationManager.AppSettings["SourceServer"];

        /// <summary>
        /// Database name for source DB.
        /// </summary>
        private static readonly string _sourceDb = ConfigurationManager.AppSettings["SourceDb"];

        /// <summary>
        /// Name for message queue schema.
        /// </summary>
        private static readonly string _messageQueueSchema = ConfigurationManager.AppSettings["MessageQueueSchema"];

        /// <summary>
        /// Name for message queue.
        /// </summary>
        private static readonly string _messageQueueName = ConfigurationManager.AppSettings["MessageQueueName"];

        /// <summary>
        /// Application name used when connecting to SQL Server.
        /// </summary>
        private static readonly string _applicationName = ConfigurationManager.AppSettings["ApplicationName"];

        /// <summary>
        /// Timeout value in milliseconds for the <c>RECEIVE</c> statement to wait for messages.
        /// </summary>
        private static readonly int _waitforTimeout = Convert.ToInt32(ConfigurationManager.AppSettings["WaitForTimeout"]);

        /// <summary>
        /// SQL Server message type name for predefined end dialog messages.
        /// </summary>
        private static readonly string _endDialogMessageType = ConfigurationManager.AppSettings["EndDialogMessageType"];

        /// <summary>
        /// SQL Server message type name for predefined conversation error messages.
        /// </summary>
        private static readonly string _errorMessageType = ConfigurationManager.AppSettings["ErrorMessageType"];

        /// <summary>
        /// Error log file path.
        /// </summary>
        private static readonly string _errorLogPath = ConfigurationManager.AppSettings["ErrorLogPath"];

        /// <summary>
        /// Source DB connection string builder.
        /// </summary>
        private static readonly SqlConnectionStringBuilder _csbSource = new SqlConnectionStringBuilder();

        private static RedisClient _rc = new RedisClient("192.168.1.134", 6379);

        /// <summary>
        /// The main entry point of this console application.
        /// </summary>
        /// <param name="args">List of arguments input from the user.</param>
        public static void Main(string[] args)
        {
            _csbSource.ApplicationName = _applicationName;
            _csbSource.DataSource = _sourceServer;
            _csbSource.InitialCatalog = _sourceDb;
            _csbSource.IntegratedSecurity = true;
            _csbSource.MultipleActiveResultSets = true;
            
            try
            {
                ProcessRequests();
            }
            catch (Exception ex)
            {
                var sb = new StringBuilder();
                sb.AppendLine("----------");
                sb.AppendLine(ex.Message);
                sb.AppendLine();
                sb.AppendLine(ex.StackTrace);
                sb.AppendLine("==========");

                File.AppendAllText(String.Format(_errorLogPath, DateTime.Today), sb.ToString());
            }
        }

        /// <summary>
        /// Processes the request from Service Broker.
        /// </summary>
        private static void ProcessRequests()
        {
            try
            {
                using (var conn = new SqlConnection(_csbSource.ToString()))
                {
                    conn.Open();

                    bool messageReceived;
                    do
                    {
                        messageReceived = false;
                        using (var transaction = conn.BeginTransaction())
                        using (var command = conn.CreateCommand())
                        {
                            command.Transaction = transaction;

                            // Even if message_body is always XML, don't cast to XML inside the RECEIVE statement as this
                            // may cause issues with activation. Convert/cast to XML after the RECEIVE is done.
                            command.CommandText = String.Format("WAITFOR (RECEIVE TOP(1) conversation_handle, message_type_name, message_body FROM {0}.{1}), TIMEOUT {2}",
                                                                BracketizeName(_messageQueueSchema),
                                                                BracketizeName(_messageQueueName),
                                                                _waitforTimeout);

                            var reader = command.ExecuteReader();
                            if (!reader.Read())
                            {
                                reader.Dispose();
                                transaction.Commit();
                                continue;
                            }

                            messageReceived = true;

                            var conversationHandle = reader.GetGuid(0);
                            var messageTypeName = reader.GetString(1);
                            var messageBody = reader.GetSqlBinary(2);

                            reader.Dispose();

                            try
                            {
                                if (messageTypeName == _endDialogMessageType || messageTypeName == _errorMessageType)
                                {
                                    if (messageTypeName == _errorMessageType)
                                    {
                                        // Handle the error message
                                        EndConversation(conn, transaction, conversationHandle);
                                        transaction.Commit();
                                    }
                                }
                                else
                                {
                                    using (var stream = new MemoryStream(messageBody.Value))
                                    {
                                        var message = XDocument.Load(stream);
                                        var responsePayload = ProcessMessage(message);
                                    }
                                }
                                EndConversation(conn, transaction, conversationHandle);
                                transaction.Commit();
                            }
                            catch
                            {
                                EndConversation(conn, transaction, conversationHandle);
                                transaction.Commit();
                                throw;
                            }
                        }
                    } while (messageReceived);
                }
            }
            catch (Exception e)
            {
                var sb = new StringBuilder();
                sb.AppendLine("======");
                sb.AppendLine(e.Message);
                sb.AppendLine();
                sb.AppendLine(e.StackTrace);
                sb.AppendLine("======");

                File.AppendAllText(String.Format(_errorLogPath, DateTime.Today), sb.ToString());
            }
        }

        private static void EndConversation(SqlConnection conn, SqlTransaction transaction, Guid conversationHandle)
        {
            using (var command = conn.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "END CONVERSATION @handle";
                command.Parameters.Add(new SqlParameter("@handle", conversationHandle));
                command.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// Services request messages by doing all the necessary computation.
        /// </summary>
        private static string ProcessMessage(XDocument xml)
        {
            var changes = xml.Root;
            var trackingType = changes.Element("TrackingType").Value;
            IEnumerable<XElement> inserted = null;
            IEnumerable<XElement> deleted = null;

            switch (trackingType)
            {
            //    case "INSERT":
            //        inserted = changes.Element("Inserted").Element("Row");
            //        break;

                case "UPDATE":
                    inserted = changes.Element("Inserted").Element("FormFieldValues").Descendants("Ffv");
                    deleted = changes.Element("Deleted").Element("FormFieldValues").Descendants("Ffv");
                    break;

            //    case "DELETE":
            //        deleted = changes.Element("Deleted").Element("Row");
            //        break;

                default:
                    throw new InvalidOperationException("Invalid tracking type");
            }

            foreach (var ffv in inserted)
            {
                var sb = new StringBuilder("FFV:");
                var formFieldValueId = ffv.Attribute("id").Value;
                var valueString = ffv.Element("ValueString").Value;
                _rc.HSet(sb.Append(formFieldValueId).ToString(), "ValueString", valueString);
            }

            // Send response back so that conversation can be closed
            return "Audit Message processed!";
        }

        /// <summary>
        /// Wraps the given Sql Server sysname in brackets and escapes any closing brackets already present in the name.
        /// </summary>
        private static string BracketizeName(string sysname)
        {
            return String.Format("[{0}]", sysname.Replace("]", "]]"));
        }
    }
}