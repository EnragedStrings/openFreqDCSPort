using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace OpenFreq.Client.NativeMethods;

/// <summary>
/// Parser for Falcon BMS StringData shared memory area
/// </summary>
public static class StringDataParser
{
    // StringIdentifier enum value for ThrTerraindir
    private const uint ThrTerraindir = 11;

    /// <summary>
    /// Parses the StringData shared memory and extracts the theater terrain directory
    /// </summary>
    /// <param name="baseAddress">Base address of the mapped string data memory</param>
    /// <returns>Theater terrain directory string, or null if not found</returns>
    public static string? ParseTheaterTerrainDir(IntPtr baseAddress)
    {
        if (baseAddress == IntPtr.Zero)
            return null;

        try
        {
            int offset = 0;

            // Read VersionNum (uint32)
            uint versionNum = (uint)Marshal.ReadInt32(baseAddress, offset);
            offset += 4;

            // Read NoOfStrings (uint32)
            uint noOfStrings = (uint)Marshal.ReadInt32(baseAddress, offset);
            offset += 4;

            // Read dataSize (uint32)
            uint dataSize = (uint)Marshal.ReadInt32(baseAddress, offset);
            offset += 4;

            // Iterate through all strings to find ThrTerraindir
            for (int i = 0; i < noOfStrings; i++)
            {
                // Read strId (uint32)
                uint strId = (uint)Marshal.ReadInt32(baseAddress, offset);
                offset += 4;

                // Read strLength (uint32)
                uint strLength = (uint)Marshal.ReadInt32(baseAddress, offset);
                offset += 4;

                // If this is ThrTerraindir, read and return it
                if (strId == ThrTerraindir)
                {
                    // Read the null-terminated string
                    byte[] stringBytes = new byte[strLength];
                    Marshal.Copy(baseAddress + offset, stringBytes, 0, (int)strLength);
                    
                    // Convert to string (ASCII/UTF-8)
                    return Encoding.UTF8.GetString(stringBytes).TrimEnd('\0');
                }

                // Skip this string's data (strLength + 1 for null terminator)
                offset += (int)strLength + 1;
            }

            // ThrTerraindir not found
            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Gets all strings from the StringData shared memory (for debugging)
    /// </summary>
    public static Dictionary<uint, string> ParseAllStrings(IntPtr baseAddress)
    {
        var result = new Dictionary<uint, string>();
        
        if (baseAddress == IntPtr.Zero)
            return result;

        try
        {
            int offset = 0;

            uint versionNum = (uint)Marshal.ReadInt32(baseAddress, offset);
            offset += 4;

            uint noOfStrings = (uint)Marshal.ReadInt32(baseAddress, offset);
            offset += 4;

            uint dataSize = (uint)Marshal.ReadInt32(baseAddress, offset);
            offset += 4;

            for (int i = 0; i < noOfStrings; i++)
            {
                uint strId = (uint)Marshal.ReadInt32(baseAddress, offset);
                offset += 4;

                uint strLength = (uint)Marshal.ReadInt32(baseAddress, offset);
                offset += 4;

                byte[] stringBytes = new byte[strLength];
                Marshal.Copy(baseAddress + offset, stringBytes, 0, (int)strLength);
                
                string value = Encoding.UTF8.GetString(stringBytes).TrimEnd('\0');
                result[strId] = value;

                offset += (int)strLength + 1;
            }
        }
        catch (Exception)
        {
            // Return partial results
        }

        return result;
    }
}
