using System;

namespace SharpOS.Kernel.DriverSystem.Drivers.Block
{
	public interface IBlockDeviceController
	{
		int Open (uint drive);
		int Release (uint drive);

		bool ReadBlock (uint drive, uint lba, uint count, MemoryBlock memory);
		bool WriteBlock (uint drive, uint lba, uint count, MemoryBlock memory);

		uint GetBlockSize (uint drive);
		uint GetTotalBlocks (uint drive);
		bool CanWrite (uint drive);

		IDevice GetDeviceDriver();
	}
}