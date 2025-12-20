using System.Threading;
using System.Threading.Tasks;

namespace HighPerfFileCopyLib
{
    public interface IChecksumAlgorithm
    {
        Task<string> ComputeAsync(string filePath, int bytesToRead, CancellationToken cancellationToken);
    }
}
