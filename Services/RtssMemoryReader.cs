using System;
using System.IO;
using System.IO.MemoryMappedFiles;

namespace EveMultiPreview.Services;

/// <summary>
/// Reads current Framerate via the RTSS instance's Shared Memory (RTSSSharedMemoryV2).
/// Parses the RTSS_SHARED_MEMORY header and loops app entries matching by PID.
/// </summary>
public static class RtssMemoryReader
{
    private const string MapName = "RTSSSharedMemoryV2";
    private const uint Signature = 0x52545353; // 'RTSS' in ASCII packed by MSVC

    /// <summary>
    /// Gets the current frame rate for all running processes managed by RTSS.
    /// Returns an empty dictionary if RTSS is not running.
    /// </summary>
    public static System.Collections.Generic.Dictionary<int, double> GetAllFps()
    {
        var result = new System.Collections.Generic.Dictionary<int, double>();

        try
        {
            using var mmf = MemoryMappedFile.OpenExisting(MapName, MemoryMappedFileRights.Read);
            using var accessor = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);

            // Read Signature
            uint sig = accessor.ReadUInt32(0);
            if (sig != Signature) return result;
            
            // Read App array offsets
            uint appEntrySize = accessor.ReadUInt32(8);
            uint appArrOffset = accessor.ReadUInt32(12);
            uint appArrSize = accessor.ReadUInt32(16); // max entries

            for (int i = 0; i < appArrSize; i++)
            {
                long entryOffset = appArrOffset + (i * appEntrySize);
                
                uint pid = accessor.ReadUInt32(entryOffset);
                if (pid > 0)
                {
                    uint frameTime = accessor.ReadUInt32(entryOffset + 280);
                    if (frameTime > 0)
                    {
                        result[(int)pid] = 1000000.0 / frameTime;
                        continue;
                    }
                    
                    // Fallback to traditional block delta
                    uint time0 = accessor.ReadUInt32(entryOffset + 268);
                    uint time1 = accessor.ReadUInt32(entryOffset + 272);
                    uint frames = accessor.ReadUInt32(entryOffset + 276);
                    
                    if (time1 > time0)
                    {
                        result[(int)pid] = 1000.0 * frames / (time1 - time0);
                    }
                    else
                    {
                        result[(int)pid] = 0;
                    }
                }
            }
        }
        catch (FileNotFoundException)
        {
            // RTSS is not running or map not created
        }
        catch
        {
            // Permission or format errors
        }

        return result;
    }
}
