//▼ ADD 2024.01.17 Fixture@Test 夹具：SQLite の最小スタブ
namespace Mono.Data.Sqlite
{
    public class SqliteConnection
    {
        public SqliteConnection(string connectionString) { }
    }

    // 03 §20: the SQL text is a string literal passed to the command.
    public class SQLiteCommand
    {
        public SQLiteCommand(string sql, object connection)
        {
            CommandText = sql;
        }

        public string CommandText { get; set; }

        public int ExecuteNonQuery() => 0;

        public object ExecuteScalar() => null;
    }
}
//▲ ADD 2024.01.17 Fixture@Test 夹具
