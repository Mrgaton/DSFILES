using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DSFiles_Shared
{
    public static class Snowflake
    {
        public const long DiscordEpochMs = 1420070400000L;

        private const int TimestampBits = 42;
        private const int WorkerBits = 5;
        private const int ProcessBits = 5;
        private const int IncrBits = 12;

        private const ulong MaxTimestampField = (1UL << TimestampBits) - 1; // 42 bits
        private const int MaxWorker = (1 << WorkerBits) - 1;      // 31
        private const int MaxProcess = (1 << ProcessBits) - 1;     // 31
        private const int MaxIncrement = (1 << IncrBits) - 1;        // 4095
        public static Parts Decompose(ulong snowflake)
        {
            ulong msSinceDiscordEpoch = (snowflake >> 22);
            int workerId = (int)((snowflake >> 17) & 0x1F);
            int processId = (int)((snowflake >> 12) & 0x1F);
            int increment = (int)(snowflake & 0xFFF);


            return new Parts(
                Raw: snowflake,
                MillisecondsSinceDiscordEpoch: msSinceDiscordEpoch,
                WorkerId: workerId,
                ProcessId: processId,
                Increment: increment
            );
        }

        public static Parts Decompose(string snowflakeString)
        {
            if (!ulong.TryParse(snowflakeString, out var value))
                throw new FormatException("Snowflake must be an unsigned 64-bit decimal number.");

            return Decompose(value);
        }
        private static void ValidateWorker(int workerId)
        {
            if ((uint)workerId > MaxWorker)
                throw new ArgumentOutOfRangeException(nameof(workerId), $"WorkerId must be 0..{MaxWorker}.");
        }

        private static void ValidateProcess(int processId)
        {
            if ((uint)processId > MaxProcess)
                throw new ArgumentOutOfRangeException(nameof(processId), $"ProcessId must be 0..{MaxProcess}.");
        }

        private static void ValidateIncrement(int increment)
        {
            if ((uint)increment > MaxIncrement)
                throw new ArgumentOutOfRangeException(nameof(increment), $"Increment must be 0..{MaxIncrement}.");
        }

        public static ulong Compose(Parts p)
        {
            ValidateTimestamp(p.MillisecondsSinceDiscordEpoch);
            ValidateWorker(p.WorkerId);
            ValidateProcess(p.ProcessId);
            ValidateIncrement(p.Increment);

            return ((p.MillisecondsSinceDiscordEpoch & MaxTimestampField) << 22)
                 | (((ulong)p.WorkerId & 0x1FUL) << 17)
                 | (((ulong)p.ProcessId & 0x1FUL) << 12)
                 | ((ulong)p.Increment & 0xFFFUL);
        }

        private static void ValidateTimestamp(ulong ms)
        {
            if (ms > MaxTimestampField)
                throw new ArgumentOutOfRangeException(nameof(ms), $"Timestamp field must fit in {TimestampBits} bits.");
        }
        public readonly record struct Parts(
            ulong Raw,
            ulong MillisecondsSinceDiscordEpoch,
            int WorkerId,
            int ProcessId,
            int Increment
        );
    }
}
