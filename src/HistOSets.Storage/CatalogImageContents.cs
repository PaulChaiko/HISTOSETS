using System.Buffers;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using static HistOSets.Storage.CatalogDatabase;

namespace HistOSets.Storage;

public sealed partial class CatalogStore
{
    // Within SQLite's default maximum row size, with headroom for SQLite record overhead.
    internal const long MaximumImageBytes = 900_000_000;

    public Stream OpenImage(Guid imageId)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var connection = Open(DatabasePath);
            try
            {
                var stream = ReadImageContent(connection, null, imageId.ToString());
                return new ConnectionImageStream(stream, connection);
            }
            catch { connection.Dispose(); throw; }
        }
    }

    internal static Stream ReadImageContent(SqliteConnection connection, SqliteTransaction? transaction, string imageId)
    {
        using var command = Command(connection, """
            SELECT c.rowid,length(c.data),i.byte_size FROM image_contents c JOIN images i ON i.id=c.image_id WHERE c.image_id=$id;
            """, transaction, ("$id", imageId));
        long row;
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read() || reader.GetInt64(1) != reader.GetInt64(2))
                throw new CatalogStorageException("Оригинал изображения отсутствует или повреждён.");
            row = reader.GetInt64(0);
        }
        return new SqliteBlob(connection, "image_contents", "data", row, readOnly: true);
    }

    internal static void WriteImageContent(SqliteConnection connection, SqliteTransaction transaction, string imageId, Stream source,
        long byteSize, string sha256)
    {
        if (byteSize <= 0 || byteSize > MaximumImageBytes)
            throw new CatalogStorageException("Размер одного оригинала превышает допустимый предел 900 МБ или равен нулю.");
        Execute(connection, "INSERT INTO image_contents(image_id,data) VALUES($id,zeroblob($size));", transaction,
            ("$id", imageId), ("$size", byteSize));
        var row = Convert.ToInt64(Scalar(connection, "SELECT last_insert_rowid();", transaction));
        using var destination = new SqliteBlob(connection, "image_contents", "data", row);
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            long total = 0;
            int count;
            while ((count = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                total += count;
                if (total > byteSize) throw new CatalogStorageException("Размер исходного изображения изменился.");
                digest.AppendData(buffer, 0, count);
                destination.Write(buffer, 0, count);
            }
            if (total != byteSize || !string.Equals(Convert.ToHexStringLower(digest.GetHashAndReset()), sha256, StringComparison.OrdinalIgnoreCase))
                throw new CatalogStorageException("Контрольная сумма исходного изображения не совпадает.");
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    internal static void VerifyImageContents(SqliteConnection connection, SqliteTransaction? transaction = null, bool hash = false)
    {
        using var command = Command(connection, """
            SELECT i.id,i.sha256,i.byte_size,length(c.data) FROM images i LEFT JOIN image_contents c ON c.image_id=i.id;
            """, transaction);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetString(0);
            if (reader.IsDBNull(3) || reader.GetInt64(2) != reader.GetInt64(3))
                throw new CatalogStorageException("В каталоге отсутствует полный оригинал изображения: " + id);
            if (!hash) continue;
            using var stream = ReadImageContent(connection, transaction, id);
            if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(stream)), reader.GetString(1), StringComparison.OrdinalIgnoreCase))
                throw new CatalogStorageException("Не совпадает контрольная сумма оригинала изображения: " + id);
        }
    }

    private sealed class ConnectionImageStream(Stream stream, SqliteConnection connection) : Stream
    {
        public override bool CanRead => stream.CanRead;
        public override bool CanSeek => stream.CanSeek;
        public override bool CanWrite => false;
        public override long Length => stream.Length;
        public override long Position { get => stream.Position; set => stream.Position = value; }
        public override int Read(byte[] buffer, int offset, int count) => stream.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => stream.Seek(offset, origin);
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { stream.Dispose(); }
                finally { connection.Dispose(); }
            }
            base.Dispose(disposing);
        }
    }
}
