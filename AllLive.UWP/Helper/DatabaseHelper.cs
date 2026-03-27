using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Windows.Storage;
using System.IO;
using AllLive.UWP.Models;

namespace AllLive.UWP.Helper
{

    public static class DatabaseHelper
    {
        static SqliteConnection db;

        // 串行化所有数据库访问，防止多线程/多 Task 并发竞争
        private static readonly object _syncLock = new object();

        public async static Task InitializeDatabase()
        {
            await ApplicationData.Current.LocalFolder.CreateFileAsync("alllive.db", CreationCollisionOption.OpenIfExists);
            string dbPath = Path.Combine(ApplicationData.Current.LocalFolder.Path, "alllive.db");
            // 添加 UTF-8 编码支持
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadWriteCreate
            }.ToString();
            string tableCommand = @"CREATE TABLE IF NOT EXISTS Favorite (
id INTEGER PRIMARY KEY AUTOINCREMENT, 
user_name TEXT,
site_name TEXT,
photo TEXT,
room_id TEXT);

CREATE TABLE IF NOT EXISTS History (
id INTEGER PRIMARY KEY AUTOINCREMENT, 
user_name TEXT,
site_name TEXT,
photo TEXT,
room_id TEXT,
watch_time DATETIME);
";
            lock (_syncLock)
            {
                if (db != null)
                {
                    return;
                }

                var connection = new SqliteConnection(connectionString);
                var initialized = false;
                try
                {
                    connection.Open();

                    using (var createTable = new SqliteCommand(tableCommand, connection))
                    {
                        createTable.ExecuteNonQuery();
                    }

                    db = connection;
                    initialized = true;
                }
                finally
                {
                    if (!initialized)
                    {
                        connection.Dispose();
                    }
                }
            }
        }

        public static void AddFavorite(FavoriteItem item)
        {
            // 空值检查
            if (string.IsNullOrEmpty(item.RoomID) || string.IsNullOrEmpty(item.SiteName))
            {
                return;
            }

            lock (_syncLock)
            {
                // 在同一锁内调用内部方法，避免重入死锁
                if (CheckFavoriteCore(item.RoomID, item.SiteName) != null) { return; }
                using (var command = new SqliteCommand())
                {
                    command.Connection = db;
                    command.CommandText = "INSERT INTO Favorite VALUES (NULL,@user_name,@site_name, @photo, @room_id);";
                    command.Parameters.AddWithValue("@user_name", item.UserName ?? "");
                    command.Parameters.AddWithValue("@site_name", item.SiteName);
                    command.Parameters.AddWithValue("@photo", item.Photo ?? "");
                    command.Parameters.AddWithValue("@room_id", item.RoomID);
                    command.ExecuteNonQuery();
                }
            }
        }

        public static long? CheckFavorite(string roomId, string siteName)
        {
            if (string.IsNullOrEmpty(roomId) || string.IsNullOrEmpty(siteName))
            {
                return null;
            }
            lock (_syncLock)
            {
                return CheckFavoriteCore(roomId, siteName);
            }
        }

        // 内部版本：调用方已持有 _syncLock，直接执行查询
        private static long? CheckFavoriteCore(string roomId, string siteName)
        {
            using (var command = new SqliteCommand())
            {
                command.Connection = db;
                command.CommandText = "SELECT id FROM Favorite WHERE room_id=@room_id and site_name=@site_name";
                command.Parameters.AddWithValue("@site_name", siteName);
                command.Parameters.AddWithValue("@room_id", roomId);
                var result = command.ExecuteScalar();
                if (result == null)
                {
                    return null;
                }
                return (long)result;
            }
        }

        public static void DeleteFavorite(long id)
        {
            lock (_syncLock)
            {
                using (var command = new SqliteCommand())
                {
                    command.Connection = db;
                    command.CommandText = "DELETE FROM Favorite WHERE id=@id";
                    command.Parameters.AddWithValue("@id", id);
                    command.ExecuteNonQuery();
                }
            }
        }

        public static void DeleteFavorite()
        {
            lock (_syncLock)
            {
                using (var command = new SqliteCommand())
                {
                    command.Connection = db;
                    command.CommandText = "DELETE FROM Favorite";
                    command.ExecuteNonQuery();
                }
            }
        }

        public static Task<List<FavoriteItem>> GetFavorites()
        {
            return Task.Run(() =>
            {
                lock (_syncLock)
                {
                    var favoriteItems = new List<FavoriteItem>();
                    using (var command = new SqliteCommand("SELECT * FROM Favorite", db))
                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            favoriteItems.Add(new FavoriteItem()
                            {
                                ID = reader.GetInt32(0),
                                RoomID = reader.GetString(4),
                                Photo = reader.GetString(3),
                                SiteName = reader.GetString(2),
                                UserName = reader.GetString(1)
                            });
                        }
                    }
                    return favoriteItems;
                }
            });
        }

        public static void AddHistory(HistoryItem item)
        {
            // 空值检查，防止 SQLite 参数绑定失败
            if (string.IsNullOrEmpty(item.RoomID) || string.IsNullOrEmpty(item.SiteName))
            {
                return;
            }

            lock (_syncLock)
            {
                // 在同一锁内调用内部方法，避免重入死锁
                var hisId = CheckHistoryCore(item.RoomID, item.SiteName);
                if (hisId != null)
                {
                    // 更新时间和用户信息
                    using (var command = new SqliteCommand())
                    {
                        command.Connection = db;
                        command.CommandText = "UPDATE History SET watch_time=@time, user_name=@user_name, photo=@photo WHERE room_id=@room_id and site_name=@site_name";
                        command.Parameters.AddWithValue("@site_name", item.SiteName);
                        command.Parameters.AddWithValue("@room_id", item.RoomID);
                        command.Parameters.AddWithValue("@time", DateTime.Now);
                        command.Parameters.AddWithValue("@user_name", item.UserName ?? "");
                        command.Parameters.AddWithValue("@photo", item.Photo ?? "");
                        command.ExecuteNonQuery();
                    }
                    return;
                }

                using (var command = new SqliteCommand())
                {
                    command.Connection = db;
                    command.CommandText = "INSERT INTO History VALUES (NULL,@user_name,@site_name, @photo, @room_id,@time);";
                    command.Parameters.AddWithValue("@user_name", item.UserName ?? "");
                    command.Parameters.AddWithValue("@site_name", item.SiteName);
                    command.Parameters.AddWithValue("@photo", item.Photo ?? "");
                    command.Parameters.AddWithValue("@room_id", item.RoomID);
                    command.Parameters.AddWithValue("@time", DateTime.Now);
                    command.ExecuteNonQuery();
                }
            }
        }

        public static long? CheckHistory(string roomId, string siteName)
        {
            if (string.IsNullOrEmpty(roomId) || string.IsNullOrEmpty(siteName))
            {
                return null;
            }
            lock (_syncLock)
            {
                return CheckHistoryCore(roomId, siteName);
            }
        }

        // 内部版本：调用方已持有 _syncLock，直接执行查询
        private static long? CheckHistoryCore(string roomId, string siteName)
        {
            using (var command = new SqliteCommand())
            {
                command.Connection = db;
                command.CommandText = "SELECT id FROM History WHERE room_id=@room_id and site_name=@site_name";
                command.Parameters.AddWithValue("@site_name", siteName);
                command.Parameters.AddWithValue("@room_id", roomId);
                var result = command.ExecuteScalar();
                if (result == null)
                {
                    return null;
                }
                return (long)result;
            }
        }

        public static void DeleteHistory(long id)
        {
            lock (_syncLock)
            {
                using (var command = new SqliteCommand())
                {
                    command.Connection = db;
                    command.CommandText = "DELETE FROM History WHERE id=@id";
                    command.Parameters.AddWithValue("@id", id);
                    command.ExecuteNonQuery();
                }
            }
        }

        public static void DeleteHistory()
        {
            lock (_syncLock)
            {
                using (var command = new SqliteCommand())
                {
                    command.Connection = db;
                    command.CommandText = "DELETE FROM History";
                    command.ExecuteNonQuery();
                }
            }
        }

        public static Task<List<HistoryItem>> GetHistory()
        {
            return Task.Run(() =>
            {
                lock (_syncLock)
                {
                    var historyItems = new List<HistoryItem>();
                    using (var command = new SqliteCommand("SELECT * FROM History ORDER BY watch_time DESC", db))
                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            historyItems.Add(new HistoryItem()
                            {
                                ID = reader.GetInt32(0),
                                RoomID = reader.GetString(4),
                                Photo = reader.GetString(3),
                                SiteName = reader.GetString(2),
                                UserName = reader.GetString(1),
                                WatchTime = reader.GetDateTime(5)
                            });
                        }
                    }
                    return historyItems;
                }
            });
        }

    }


}
