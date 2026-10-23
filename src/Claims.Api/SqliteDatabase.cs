using Microsoft.Data.Sqlite;

namespace Claims.Api;
internal sealed class SqliteDatabase
{
    readonly string connectionString;
    public SqliteDatabase(string path)
    {
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false
        }.ToString();
    }

    public SqliteConnection Open()
    {
        var db = new SqliteConnection(connectionString);
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=10000;";
        cmd.ExecuteNonQuery();
        return db;
    }

    public static SqliteCommand Cmd(SqliteConnection db, SqliteTransaction? tx, string sql, params object? [] values)
    {
        var c = db.CreateCommand();
        c.Transaction = tx;
        c.CommandText = sql;
        for (int i = 0; i < values.Length; i++)
            c.Parameters.AddWithValue("$p" + i, values[i] ?? DBNull.Value);
        return c;
    }

    public static void Exec(SqliteConnection db, SqliteTransaction? tx, string sql, params object? [] values)
    {
        using var c = Cmd(db, tx, sql, values);
        c.ExecuteNonQuery();
    }
}
