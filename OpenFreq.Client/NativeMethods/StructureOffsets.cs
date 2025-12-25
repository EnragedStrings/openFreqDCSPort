using System;
using System.Runtime.InteropServices;

namespace OpenFreq.Client.NativeMethods;

/// <summary>
/// Helper utility to calculate structure field offsets
/// </summary>
public static class StructureOffsets
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BMS4FlightDataPartial
    {
        public float x;              // 0
        public float y;              // 4
        public float z;              // 8
        public float xDot;           // 12
        public float yDot;           // 16
        public float zDot;           // 20
        public float alpha;          // 24
        public float beta;           // 28
        public float gamma;          // 32
        public float pitch;          // 36
        public float roll;           // 40
        public float yaw;            // 44
        public float mach;           // 48
        public float kias;           // 52
        public float vt;             // 56
        public float gs;             // 60
        public float windOffset;     // 64
        public float nozzlePos;      // 68
        public float internalFuel;   // 72
        public float externalFuel;   // 76
        public float fuelFlow;       // 80
        public float rpm;            // 84
        public float ftit;           // 88
        public float gearPos;        // 92
        public float speedBrake;     // 96
        public float epuFuel;        // 100
        public float oilPressure;    // 104
        public uint lightBits;       // 108
        public float headPitch;      // 112
        public float headRoll;       // 116
        public float headYaw;        // 120
        public uint lightBits2;      // 124
        public uint lightBits3;      // 128
        public float ChaffCount;     // 132
        public float FlareCount;     // 136
        public float NoseGearPos;    // 140
        public float LeftGearPos;    // 144
        public float RightGearPos;   // 148
        public float AdiIlsHorPos;   // 152
        public float AdiIlsVerPos;   // 156
        public int courseState;      // 160
        public int headingState;     // 164
        public int totalStates;      // 168
        public float courseDeviation;     // 172
        public float desiredCourse;       // 176
        public float distanceToBeacon;    // 180
        public float bearingToBeacon;     // 184
        public float currentHeading;      // 188
        public float desiredHeading;      // 192
        public float deviationLimit;      // 196
        public float halfDeviationLimit;  // 200
        public float localizerCourse;     // 204
        public float airbaseX;            // 208
        public float airbaseY;            // 212
        public float totalValues;         // 216
        public float TrimPitch;      // 220
        public float TrimRoll;       // 224
        public float TrimYaw;        // 228
        public uint hsiBits;         // 232 <-- THIS IS WHAT WE NEED
    }

    /// <summary>
    /// Gets the offset of hsiBits in the BMS4FlightData structure
    /// </summary>
    public static int HsiBitsOffset => 232;

    /// <summary>
    /// Prints all calculated offsets for debugging
    /// </summary>
    public static void PrintOffsets()
    {
        Console.WriteLine("BMS4FlightData Field Offsets:");
        Console.WriteLine($"  x: 0");
        Console.WriteLine($"  y: 4");
        Console.WriteLine($"  z: 8");
        Console.WriteLine($"  hsiBits: {HsiBitsOffset}");
    }
}
