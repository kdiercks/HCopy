using System;
using System.Buffers;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace HighPerfFileCopyLib
{
    public class Md5ChecksumAlgorithm : IChecksumAlgorithm
    {
        public async Task<string> ComputeAsync(string filePath, int bytesToRead, CancellationToken cancellationToken)
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            int toRead = bytesToRead;
            if (stream.Length < bytesToRead)
                toRead = (int)stream.Length;

            byte[] buffer = ArrayPool<byte>.Shared.Rent(toRead);
            try
            {
                int read = await stream.ReadAsync(buffer, 0, toRead, cancellationToken).ConfigureAwait(false);
                using var md5 = MD5.Create();
                byte[] hash = md5.ComputeHash(buffer, 0, read);
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }
}
