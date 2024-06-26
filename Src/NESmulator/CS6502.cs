﻿using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using log4net;

namespace NESEmulator
{
    [Flags]
    public enum FLAGS6502
    {
        /// <summary>
        /// Carry
        /// </summary>
        C = (1 << 0),
        /// <summary>
        /// Zero
        /// </summary>
        Z = (1 << 1),
        /// <summary>
        /// Interrupt disable
        /// </summary>
        I = (1 << 2),
        /// <summary>
        /// Decimal (unused right now)
        /// </summary>
        D = (1 << 3),
        /// <summary>
        /// Break
        /// </summary>
        B = (1 << 4),
        /// <summary>
        /// Unused
        /// </summary>
        U = (1 << 5),
        /// <summary>
        /// Overflow
        /// </summary>
        V = (1 << 6),
        /// <summary>
        /// Negative
        /// </summary>
        N = (1 << 7)
    }

    public class CS6502 : BusDevice
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(CS6502));

        public BusDeviceType DeviceType { get { return BusDeviceType.CPU; } }

        private List<Instruction> opcode_lookup;
        private IBus bus;

        #region Emulator vars
        private byte   fetched = 0x00;          // Represents the working input value to the ALU
        private ushort temp = 0x0000;           // Just a temp var
        private ushort addr_abs = 0x0000;       // Absolute memory address
        private sbyte  addr_rel = 0x00;         // Relative memory address
        private byte   opcode = 0x00;           // Current instruction
        private byte   cycles = 0;              // Counts how many cycles the instruction has remaining
        private uint   clock_count = 0;         // A global accumulation of the number of clocks
        private byte   _instCycleCount = 0;
        private bool   _nmiPending = false;
        private bool   _nmiServicing = false;
        private bool   _irqPending = false;
        private bool   _irqDisablePending = false;
        private bool   _irqServicing = false;
        private byte   _irqEnableLatency = 0;
        private ushort _irqVector = ADDR_IRQ;
        private bool   _startCountingIRQs = true;
        private int    _irqCount = 0;
        /// <summary>
        /// Intermediate data needed between cycles of an instruction
        /// </summary>
        private Dictionary<string, object> instr_state = new Dictionary<string, object>();
        private Dictionary<string, byte>   instr_state_bytes = new Dictionary<string, byte>();
        private Dictionary<string, ushort> instr_state_ushort = new Dictionary<string, ushort>();

        private const string STATE_ADDR_MODE_COMPLETED_CYCLE = "opcode_start_cycle";
        #endregion // Emulator vars

        #region Debugging
        public bool ServicingIRQ { get => _irqServicing; }
        #endregion // Debugging

        #region Well-Known Addresses

        /// <summary>
        /// Address of bottom of stack
        /// </summary>
        public const ushort ADDR_STACK = 0x0100;

        /// <summary>
        /// Address of program counter
        /// </summary>
        public const ushort ADDR_PC = 0xFFFC;
        
        /// <summary>
        /// Address of code for IRQ
        /// </summary>
        public const ushort ADDR_IRQ = 0xFFFE;

        /// <summary>
        /// Address of code for NMI
        /// </summary>
        public const ushort ADDR_NMI = 0xFFFA;

        #endregion // Well-Known Addresses

        #region DMA Attributes

        /// <summary>
        /// Indicates if OAMDMA transfer is in progress
        /// </summary>
        public bool DMATransfer { get; private set; }
        private byte _dmaPage;
        private byte _dmaStartAddr;
        private byte _dmaAddr;
        private byte _dmaData;
        private bool _dmaSync = false;

        #endregion // DMA Attributes

        #region Memory Reader Attributes

        public MemoryReader ExternalMemoryReader;
        private bool _readerFetch = false;

        #endregion // DMC Attributes

        public CS6502()
        {
            build_lookup();
            ExternalMemoryReader = new MemoryReader();
            ExternalMemoryReader.MemoryReadRequest += ExternalMemoryReader_MemoryReadRequest;
        }

        private void ExternalMemoryReader_MemoryReadRequest(object sender, EventArgs e)
        {
            _readerFetch = true;
            ExternalMemoryReader.CyclesToComplete = 4;
        }

        public void SignalNMI() => _nmiPending = true;
        public void SignalIRQ() => _irqPending = true;
        public void ClearIRQ() => _irqPending = false;

        public void ConnectBus(IBus bus)
        {
            this.bus = bus;
        }

        #region Register Properties
        public byte a { get; set; }
        public byte x { get; set; }
        public byte y { get; set; }
        public byte sp { get; set; }
        public ushort pc { get; set; }
        public FLAGS6502 status { get; set; }
        #endregion // Register Properties


        /// <summary>
        /// Reset CPU to known state
        /// </summary>
        /// <remarks>
        /// This is hard-wired inside the CPU. The status register remains the same except for unused
        /// bit which remains at 1, and interrupt inhibit which is set to 1 as well. An 
        /// absolute address is read from location 0xFFFC
        /// which contains a second address that the program counter is set to. This 
        /// allows the programmer to jump to a known and programmable location in the
        /// memory to start executing from. Typically the programmer would set the value
        /// at location 0xFFFC at compile time.
        /// </remarks>
        public void Reset()
        {
            // Reset DMA
            cycles   = 0;
            _instCycleCount = 0;
            _dmaAddr = 0;
            _dmaData = 0;
            _dmaPage = 0;
            _dmaSync = false;
            DMATransfer = false;

            // Set PC
            addr_abs = ADDR_PC;
            ushort lo = read(addr_abs);
            ushort hi = read((ushort)(addr_abs + 1));
            pc = (ushort)((hi << 8) | lo);
            Log.Info($"Cartridge starts at {pc:X4}");

            // Reset internal registers
            a = x = y = 0;
            sp = 0xFD;
            status |= (FLAGS6502.U | FLAGS6502.I);

            // Clear internal helper variables
            addr_abs = 0x0000;
            addr_rel = 0x00;
            fetched = 0x00;

            _irqPending = false;
            _irqServicing = false;
            _nmiPending = false;
            _nmiPending = false;

            clock_count = 0;
        }

        public void PowerOn()
        {
            // https://wiki.nesdev.org/w/index.php?title=CPU_power_up_state
            // "P = $34"
            status = FLAGS6502.I | FLAGS6502.B | FLAGS6502.U;

            // Set PC
            addr_abs = ADDR_PC;
            ushort lo = read(addr_abs);
            ushort hi = read((ushort)(addr_abs + 1));
            pc = (ushort)((hi << 8) | lo);

            //pc = 0xC000;
            Log.Info($"Cartridge starts at {pc:X4}");

            // Set internal registers
            sp = 0xFD;
        }

        /// <summary>
        /// Interrupt Request
        /// </summary>
        /// <remarks>
        /// Interrupt requests are a complex operation and only happen if the
        /// "disable interrupt" flag is 0. IRQs can happen at any time, but
        /// you dont want them to be destructive to the operation of the running 
        /// program. Therefore the current instruction is allowed to finish
        /// (which I facilitate by doing the whole thing when cycles == 0) and 
        /// then the current program counter is stored on the stack. Then the
        /// current status register is stored on the stack. When the routine
        /// that services the interrupt has finished, the status register
        /// and program counter can be restored to how they where before it 
        /// occurred. This is impemented by the "RTI" instruction. Once the IRQ
        /// has happened, in a similar way to a reset, a programmable address
        /// is read form hard coded location 0xFFFE, which is subsequently
        /// set to the program counter.
        /// </remarks>
        public void IRQ()
        {
            //if (_startCountingIRQs)
            //{
            //    _irqCount++;
            //    Log.Debug($"IRQCount = {_irqCount}");
            //}

            if (cycles == 0)
            {
                //Log.Debug($"[{clock_count}] Servicing IRQ");
                _irqVector = ADDR_IRQ;
                if (_nmiPending)
                {
                    Log.Debug($"[{clock_count}] IRQ/BRK has been hijacked by NMI!");
                    _irqVector = ADDR_NMI;
                }
                // IRQ cycles
                _instCycleCount = 7;
                instr_state.Clear();
            }
            else if (cycles == 2)
            {
                if (_nmiPending)
                {
                    Log.Debug($"[{clock_count}] IRQ/BRK has been hijacked by NMI!");
                    _irqVector = ADDR_NMI;
                }
                // Push the PC to the stack. It is 16-bits, so requires 2 pushes
                push((byte)((pc >> 8) & 0x00FF));
            }
            else if (cycles == 3)
            {
                if (_nmiPending)
                {
                    Log.Debug($"[{clock_count}] IRQ/BRK has been hijacked by NMI!");
                    _irqVector = ADDR_NMI;
                }
                push((byte)(pc & 0x00FF));
            }
            else if (cycles == 4)
            {
                if (_nmiPending)
                {
                    Log.Debug($"[{clock_count}] IRQ/BRK has been hijacked by NMI!");
                    _irqVector = ADDR_NMI;
                }
                // Then push status register to the stack
                setFlag(FLAGS6502.B, false);
                setFlag(FLAGS6502.U, true);
                push((byte)status);
                setFlag(FLAGS6502.I, true);
            }
            else if (cycles == 5)
            {
                // Read new PC location from fixed address
                addr_abs = _irqVector;
                ushort lo = read(addr_abs);
                instr_state_ushort["lo"] = lo;
            }
            else if (cycles == 6)
            {
                ushort hi = read((ushort)(addr_abs + 1));
                pc = (ushort)((hi << 8) | instr_state_ushort["lo"]);
                _irqServicing = false;
            }
        }

        /// <summary>
        /// Non-maskable Interrupt Request
        /// </summary>
        /// <remarks>
        /// A Non-Maskable Interrupt cannot be ignored. It behaves in exactly the
        /// same way as a regular IRQ, but reads the new program counter address
        /// from location 0xFFFA.
        /// </remarks>
        public void NMI()
        {
            if (cycles == 0)
            {
                // NMI cycles
                _instCycleCount = 7;
                _nmiPending = false;
                instr_state.Clear();
            }
            else if (cycles == 2)
            {
                // Push the PC to the stack. It is 16-bits, so requires 2 pushes
                push((byte)((pc >> 8) & 0x00FF));
            }
            else if (cycles == 3)
            {
                push((byte)(pc & 0x00FF));
            }
            else if (cycles == 4)
            {
                // Then push status register to the stack
                setFlag(FLAGS6502.B, false);
                setFlag(FLAGS6502.U, true);
                push((byte)status);
                // "IRQ will be executed only when the I flag is clear.IRQ and BRK both set the I flag, whereas the NMI does not
                // affect its state. (https://www.nesdev.org/6502_cpu.txt)
                //setFlag(FLAGS6502.I, true);
            }
            else if (cycles == 5)
            {
                // Read new PC location from fixed address
                addr_abs = ADDR_NMI;
                ushort lo = read(addr_abs);
                instr_state_ushort["lo"] = lo;
            }
            else if (cycles == 6)
            { 
                ushort hi = read((ushort)(addr_abs + 1));
                pc = (ushort)((hi << 8) | instr_state_ushort["lo"]);
                _nmiServicing = false;
            }
        }

        /// <summary>
        /// Who knew interrupting code could be so complicated?
        /// </summary>
        /// <param name="midInstructionCycle">
        /// True if we are calling this function in the middle of an instruction, false if beginning.
        /// This is important because we do not want to alter intermediate states between instructions.
        /// </param>
        private bool pollForIRQ(bool midInstructionCycle = false)
        {
            // OMG!! I know you don't want to be interrupted, Mr. CPU - but I JUST asked for an IRQ!!
            if (getFlag(FLAGS6502.I) == 1 && bus.IsDeviceAssertingIRQ() && _irqDisablePending)
            {
                if (!midInstructionCycle)
                    _irqDisablePending = false;
                // Fine - this is the LAST ONE. After this, I'm cutting you off!
                return true;
            }
            else if (getFlag(FLAGS6502.I) == 0)
            {
                if (bus.IsDeviceAssertingIRQ() && _irqEnableLatency == 0)
                {
                    return true;
                }

                if (!midInstructionCycle && _irqEnableLatency > 0)
                    --_irqEnableLatency;

                return false;
            }

            if (!midInstructionCycle)
                _irqDisablePending = false;

            return false;
        }
        (uint, string sInst) disassembled;

        /// <summary>
        /// Perform clock cycle
        /// </summary>
        /// <remarks>
        /// Each instruction requires a variable number of clock cycles to execute.
        /// In my emulation, I only care about the final result and so I perform
        /// the entire computation in one hit. In hardware, each clock cycle would
        /// perform "microcode" style transformations of the CPUs state.
        ///
        /// To remain compliant with connected devices, it's important that the 
        /// emulation also takes "time" in order to execute instructions, so I
        /// implement that delay by simply counting down the cycles required by 
        /// the instruction. When it reaches 0, the instruction is complete, and
        /// the next one is ready to be executed.
        /// </remarks>
        public void Clock(ulong clockCounter)
        {
            if (clockCounter % 3 != 0)
                return;

            clock_count++;

            // Read in another code-base that instructions don't start executing until after 7 cpu cycles, so here we go.
            if (clock_count < 8) return;

            if (DMATransfer)
            {
                doDMATransfer(clock_count);
            }
            else if (_readerFetch)
            {
                readerFetch(clock_count);
            }
            else
            {
                // Check if all cycles completed
                if (cycles == _instCycleCount)
                {
                    cycles = 0;
                    if (_nmiPending)
                    {
                        //Log.Debug($"[{clock_count}] Invoking NMI");
                        _nmiServicing = true;
                    }
                    else if (pollForIRQ())
                    {
                        _irqServicing = true;
                    }
                    else
                    {
                        //disassembled = Disassemble(pc);
                        //Log.Debug($"[{clock_count}] {disassembled.sInst}");

                        // Read the next instruction byte. This 8-bit value is used to index the translation
                        // table to get the relevat information about how to implement the instruction
                        opcode = read(pc);

                        // After reading opcode, increment pc
                        pc++;

                        // Make sure Unused status flag is 1
                        setFlag(FLAGS6502.U, true);

                        // Get starting number of cycles
                        _instCycleCount = opcode_lookup[opcode].cycles;

                        // Make sure Unused status flag is 1
                        setFlag(FLAGS6502.U, true);
                    }
                }

                if (_nmiServicing)
                {
                    NMI();
                }
                else if (_irqServicing)
                {
                    IRQ();
                }
                else
                {
                    if (opcode_lookup[opcode].addr_mode())
                    {
                        opcode_lookup[opcode].operation();
                    }
                }
                cycles++;
            }
        }

        /// <summary>
        /// Indicates if current instruction has completed (for stepping through code)
        /// </summary>
        /// <returns></returns>
        public bool isComplete()
        {
            return this.cycles == _instCycleCount;
        }

        /// <summary>
        /// Produces a map of strings, with keys equivalent to instruction start
        /// locations in memory.
        /// </summary>
        /// <param name="start"></param>
        /// <param name="stop"></param>
        /// <returns></returns>
        public Dictionary<ushort, string> Disassemble(ushort start, ushort stop)
        {
            uint addr = start;
            Dictionary<ushort, string> mapLines = new Dictionary<ushort, string>();
            ushort line_addr = 0;

            // Starting at the specified address we read an instruction
            // byte, which in turn yields information from the lookup table
            // as to how many additional bytes we need to read and what the
            // addressing mode is. I need this info to assemble human readable
            // syntax, which is different depending upon the addressing mode

            // As the instruction is decoded, a std::string is assembled
            // with the readable output
            while (addr <= stop)
            {
                line_addr = (ushort)addr;

                string sInst;
                (addr, sInst) = Disassemble((ushort)addr);

                // Add the formed string to a Dictionary, using the instruction's
                // address as the key. This makes it convenient to look for later
                // as the instructions are variable in length, so a straight up
                // incremental index is not sufficient.
                mapLines[line_addr] = sInst;
            }

            return mapLines;
        }

        public (uint, string) Disassemble(uint addr)
        {
            byte value = 0x00, lo = 0x00, hi = 0x00;
            ushort ptr = 0x0000;

            string hexAddr = addr.ToString("X4");

            // Read instruction, and get its readable name
            byte opcode = bus.Read((ushort)addr, true);
            addr++;

            // Get oprands from desired locations, and form the
            // instruction based upon its addressing mode. These
            // routines mimmick the actual fetch routine of the
            // 6502 in order to get accurate data as part of the
            // instruction
            string addressMode = string.Empty;
            if (opcode_lookup[opcode].addr_mode == IMP)
            {
                addressMode = "{IMP}";
            }
            else if (opcode_lookup[opcode].addr_mode == IMM)
            {
                value = bus.Read((ushort)addr, true);
                if (opcode != 0)
                {
                    addr++;
                }
                addressMode = string.Format("#${0} {{IMM}}", value.ToString("X2"));
            }
            else if (opcode_lookup[opcode].addr_mode == ZP0)
            {
                lo = bus.Read((ushort)addr, true);
                hi = 0x00;
                addr++;
                addressMode = string.Format("${0} {{ZP0}}", lo.ToString("X2"));
            }
            else if (opcode_lookup[opcode].addr_mode == ZPX)
            {
                lo = bus.Read((ushort)addr, true);
                hi = 0x00;
                addr++;
                addressMode = string.Format("${0}, X {{ZPX}}", lo.ToString("X2"));
            }
            else if (opcode_lookup[opcode].addr_mode == ZPY)
            {
                lo = bus.Read((ushort)addr, true);
                hi = 0x00;
                addr++;
                addressMode = string.Format("${0}, Y {{ZPY}}", lo.ToString("X2"));
            }
            else if (opcode_lookup[opcode].addr_mode == IZX)
            {
                lo = bus.Read((ushort)addr, true);
                addr++;
                hi = 0x00;
                ptr = (ushort)(bus.Read((ushort)((lo + x + 1) & 0x00FF), true) << 8 | bus.Read((ushort)((lo + x) & 0x00FF), true));
                value = bus.Read(ptr, true);
                addressMode = string.Format("(${0}, X) {{IZX}} @ {1} = {2} = {3}", lo.ToString("X2"), lo.ToString("X2"), ptr.ToString("X4"), value.ToString("X2"));
            }
            else if (opcode_lookup[opcode].addr_mode == IZY)
            {
                lo = bus.Read((ushort)addr, true);
                hi = 0x00;
                addr++;
                addressMode = string.Format("(${0}), Y {{IZY}}", lo.ToString("X2"));
            }
            else if (opcode_lookup[opcode].addr_mode == ABS)
            {
                lo = bus.Read((ushort)addr, true);
                addr++;
                hi = bus.Read((ushort)addr, true);
                addr++;
                value = bus.Read((ushort)(hi << 8 | lo), true);
                addressMode = string.Format("${0} {{ABS}} = {1}", ((hi << 8) | lo).ToString("X4"), value.ToString("X2"));
            }
            else if (opcode_lookup[opcode].addr_mode == ABX)
            {
                lo = bus.Read((ushort)addr, true);
                addr++;
                hi = bus.Read((ushort)addr, true);
                addr++;
                ptr = (ushort)((hi << 8) | lo);
                value = bus.Read((ushort)(ptr + x), true);
                addressMode = string.Format("${0}, X {{ABX}} @ {1} = {2}", ptr.ToString("X4"), ((ushort)(ptr + x)).ToString("X4"), value.ToString("X2"));
            }
            else if (opcode_lookup[opcode].addr_mode == ABY)
            {
                lo = bus.Read((ushort)addr, true);
                addr++;
                hi = bus.Read((ushort)addr, true);
                addr++;
                ptr = (ushort)((hi << 8) | lo);
                value = bus.Read((ushort)(ptr + y), true);
                addressMode = string.Format("${0}, Y {{ABY}} @ {1} = {2}", ptr.ToString("X4"), ((ushort)(ptr + y)).ToString("X4"), value.ToString("X2"));
            }
            else if (opcode_lookup[opcode].addr_mode == IND)
            {
                lo = bus.Read((ushort)addr, true);
                addr++;
                hi = bus.Read((ushort)addr, true);
                addr++;
                ptr = (ushort)(hi << 8 | lo);
                var addr_lo = bus.Read(ptr, true);
                var ptr_addr = 0;
                if (lo == 0xFF)
                    ptr_addr = (ushort)(bus.Read((ushort)(ptr & 0xFF00), true) << 8 | addr_lo);
                else
                    ptr_addr = (ushort)(bus.Read((ushort)(ptr + 1), true) << 8 | addr_lo);
                addressMode = string.Format("(${0}) {{IND}} = {1}", ptr.ToString("X4"), ptr_addr.ToString("X4"));
            }
            else if (opcode_lookup[opcode].addr_mode == REL)
            {
                value = bus.Read((ushort)addr, true);
                addr++;
                addressMode = string.Format("${0} [${1}] {{REL}}", value.ToString("X2"), (addr + (sbyte)value).ToString("X4"));
            }

            string sInst = string.Format("$ {0}: {1} {2}", hexAddr, opcode_lookup[opcode].name, addressMode);

            return (addr, sInst);
        }

        /// <summary>
        /// The read location of data can come from two sources:
        /// A memory address or its immediately available as part of the instruction.
        /// This function decides depending on the address mode of the instruction byte.
        /// </summary>
        /// <returns></returns>
        private byte fetch()
        {
            if (!(opcode_lookup[opcode].addr_mode == IMP ||
                  opcode_lookup[opcode].addr_mode == IZY))
                fetched = read(addr_abs);

            return fetched;
        }

        private void doDMATransfer(ulong clockCounter)
        {
            if (!_dmaSync)
            {
                if (clockCounter % 2 == 1)
                {
                    _dmaSync = true;
                    _dmaStartAddr = read(0x2003);
                    // Starting DMA address always at 0xXX00 (XX = _dmaPage)
                    _dmaAddr = 0;
                }
            }
            else
            {
                if (clockCounter % 2 == 0)
                {
                    _dmaData = read((ushort)(_dmaPage << 8 | _dmaAddr));
                    _dmaAddr++;
                }
                else
                {
                    write(0x2004, _dmaData);

                    // When we wrap back to 0, we are done.
                    if (_dmaAddr == 0)
                    {
                        DMATransfer = false;
                        _dmaSync = false;
                    }
                }
            }
        }

        private void readerFetch(ulong clockCounter)
        {
            if (clockCounter % 6 == 0)
            {
                Log.Debug($"[{clock_count}] Reading byte for DMC");
                ExternalMemoryReader.Buffer = read(ExternalMemoryReader.MemoryPtr);
                ExternalMemoryReader.BufferReady = true;
                //this.cycles = ExternalMemoryReader.CyclesToComplete;
                _instCycleCount = ExternalMemoryReader.CyclesToComplete;
                _readerFetch = false;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void clearInstrState()
        {
            instr_state.Clear();
            instr_state_bytes.Clear();
            instr_state_ushort.Clear();
        }

        #region Flag Methods

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private byte getFlag(FLAGS6502 f)
        {
            return status.HasFlag(f) ? (byte)1 : (byte)0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void setFlag(FLAGS6502 f, bool v)
        {
            if (v)
            {
                status |= f;
            }
            else
            {
                status &= ~f;
            }
        }

        #endregion // Flag Methods

        #region Bus Methods

        public bool Write(ushort addr, byte data)
        {
            // Ignore since we call BUS' Write(), which calls this.
            return false;
        }

        public bool Read(ushort addr, out byte data, bool readOnly = false)
        {
            // Ignore since we call BUS' Read(), which calls this.
            data = 0;
            return false;
        }

        /// <summary>
        /// Reads an 8-bit byte from the bus, located at the specified 16-bit address
        /// </summary>
        /// <param name="addr">Address of the byte to read</param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private byte read(ushort addr)
        {
            byte dataRead = 0x00;
            // In normal operation "read only" is set to false. This may seem odd. Some
            // devices on the bus may change state when they are read from, and this 
            // is intentional under normal circumstances. However the disassembler will
            // want to read the data at an address without changing the state of the
            // devices on the bus

            dataRead = bus.Read(addr, false);

            return dataRead;
        }

        /// <summary>
        /// Writes a byte to the bus at the specified address
        /// </summary>
        /// <param name="addr">Address to write to</param>
        /// <param name="data">The byte of data to write</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void write(ushort addr, byte data)
        {
            if (addr == 0x4014)
            {
                _dmaPage = data;
                //_dmaAddr = 0x00;
                DMATransfer = true;
            }
            else
                bus.Write(addr, data);
        }

        #endregion // Bus Methods

        #region Addressing Modes
        /*****
         * The 6502 can address between 0x0000 - 0xFFFF. The high byte is often referred
         * to as the "page", and the low byte is the offset into that page. This implies
         * there are 256 pages, each containing 256 bytes.
         * Several addressing modes have the potential to require an additional clock
         * cycle if they cross a page boundary. This is combined with several instructions
         * that enable this additional clock cycle. So each addressing function returns
         * a flag saying it has potential, as does each instruction. If both instruction
         * and address function return 1, then an additional clock cycle is required.
         *****/

        /// <summary>
        /// Address Mode: Implied
        /// </summary>
        /// <returns></returns>
        /// <remarks>
        /// There is no additional data required for this instruction. The instruction
        /// does something very simple like like sets a status bit. However, we will
        /// target the accumulator, for instructions like PHA
        /// </remarks>
        private bool IMP()
        {
            bool isComplete = false;
            switch (cycles)
            {
                case 0:
                    clearInstrState();
                    // Not technically accurate to do here, but it's fine.
                    fetched = a;
                    instr_state_bytes[STATE_ADDR_MODE_COMPLETED_CYCLE] = cycles;
                    isComplete = true;
                    break;
                default:
                    isComplete = true;
                    break;
            }

            return isComplete;
        }

        /// <summary>
        /// Address Mode: Immediate
        /// </summary>
        /// <returns></returns>
        /// <remarks>
        /// The instruction expects the next byte to be used as a value, so we'll prep
        /// the read address to point to the next byte
        /// </remarks>
        private bool IMM()
        {
            bool isComplete = false;
            switch (cycles)
            {
                case 0:
                    clearInstrState();
                    break;
                case 1:
                    addr_abs = pc++;
                    fetch();
                    instr_state_bytes[STATE_ADDR_MODE_COMPLETED_CYCLE] = cycles;
                    isComplete = true;
                    break;
                default:
                    isComplete = true;
                    break;
            }

            return isComplete;
        }

        /// <summary>
        /// Address Mode: Zero Page
        /// </summary>
        /// <returns></returns>
        /// <remarks>
        /// To save program bytes, zero page addressing allows you to absolutely address
        /// a location in first 0xFF bytes of address range. Clearly this only requires
        /// one byte instead of the usual two.
        /// </remarks>
        private bool ZP0()
        {
            bool isComplete = false;
            switch (cycles)
            {
                case 0:
                    clearInstrState();
                    break;
                case 1:
                    addr_abs = read(pc);
                    pc++;
                    addr_abs &= 0x00FF;
                    if (opcode_lookup[opcode].instr_type == CPUInstructionType.Write)
                    {
                        instr_state_bytes[STATE_ADDR_MODE_COMPLETED_CYCLE] = cycles;
                        isComplete = true;
                    }
                    break;
                case 2:
                    if (instr_state_bytes.ContainsKey(STATE_ADDR_MODE_COMPLETED_CYCLE))
                    {
                        isComplete = true;
                        break;
                    }
                    if (opcode_lookup[opcode].instr_type != CPUInstructionType.Write)
                    {
                        fetch();
                        instr_state_bytes[STATE_ADDR_MODE_COMPLETED_CYCLE] = cycles;
                    }
                    else
                    {
                        Log.Error($"[{clock_count}] ZP0 addressing mode error - extra cycle executed when it shouldn't!");
                    }
                    isComplete = true;
                    break;
                default:
                    isComplete = true;
                    break;
            }
            return isComplete;
        }

        /// <summary>
        /// Address Mode: Zero Page with X Offset
        /// </summary>
        /// <returns></returns>
        /// <remarks>
        /// Fundamentally the same as Zero Page addressing, but the contents of the X Register
        /// is added to the supplied single byte address. This is useful for iterating through
        /// ranges within the first page.
        /// </remarks>
        private bool ZPX()
        {
            bool isComplete = false;
            switch (cycles)
            {
                case 0:
                    clearInstrState();
                    break;
                case 1:
                    addr_abs = read(pc);
                    pc++;
                    break;
                case 2:
                    // The CPU performs a "dummy" read from the address here.
                    //Log.Debug($"[{clock_count}] Dummy read from addr_abs at 0x{addr_abs:X4}, index = 0x{x:X2}");
                    read(addr_abs);
                    addr_abs += x;
                    addr_abs &= 0x00FF;
                    if (opcode_lookup[opcode].instr_type == CPUInstructionType.Write)
                    {
                        instr_state_bytes[STATE_ADDR_MODE_COMPLETED_CYCLE] = cycles;
                        isComplete = true;
                    }
                    break;
                case 3:
                    if (instr_state_bytes.ContainsKey(STATE_ADDR_MODE_COMPLETED_CYCLE))
                    {
                        isComplete = true;
                        break;
                    }

                    if (opcode_lookup[opcode].instr_type != CPUInstructionType.Write)
                    {
                        fetch();
                        instr_state_bytes[STATE_ADDR_MODE_COMPLETED_CYCLE] = cycles;
                    }
                    else
                    {
                        Log.Error($"[{clock_count}] ZPX addressing mode error - extra cycle executed when it shouldn't!");
                    }
                    isComplete = true;
                    break;
                default:
                    isComplete = true;
                    break;
            }
            return isComplete;
        }

        /// <summary>
        /// Address Mode: Zero Page with Y Offset
        /// </summary>
        /// <returns></returns>
        /// <remarks>
        /// Same as Zero Page with X offset, but uses Y register for offset
        /// </remarks>
        private bool ZPY()
        {
            bool isComplete = false;
            switch (cycles)
            {
                case 0:
                    clearInstrState();
                    break;
                case 1:
                    addr_abs = read(pc);
                    pc++;
                    break;
                case 2:
                    // The CPU performs a "dummy" read from the address here.
                    //Log.Debug($"[{clock_count}] Dummy read from addr_abs at 0x{addr_abs:X4}, index = 0x{y:X2}");
                    read(addr_abs);
                    addr_abs += y;
                    addr_abs &= 0x00FF;
                    if (opcode_lookup[opcode].instr_type == CPUInstructionType.Write)
                    {
                        instr_state_bytes[STATE_ADDR_MODE_COMPLETED_CYCLE] = cycles;
                        isComplete = true;
                    }
                    break;
                case 3:
                    if (instr_state_bytes.ContainsKey(STATE_ADDR_MODE_COMPLETED_CYCLE))
                    {
                        isComplete = true;
                        break;
                    }
                    if (opcode_lookup[opcode].instr_type != CPUInstructionType.Write)
                    {
                        fetch();
                        instr_state_bytes[STATE_ADDR_MODE_COMPLETED_CYCLE] = cycles;
                    }
                    else
                    {
                        Log.Error($"[{clock_count}] ZPY addressing mode error - extra cycle executed when it shouldn't!");
                    }
                    isComplete = true;
                    break;
                default:
                    isComplete = true;
                    break;
            }
            return isComplete;
        }

        /// <summary>
        /// Address Mode: Relative
        /// </summary>
        /// <returns></returns>
        /// <remarks>
        /// This address mode is exclusive to branch instructions. The address
        /// must reside within -128 to +127 of the branch instruction, i.e.
        /// you cant directly branch to any address in the addressable range.
        /// </remarks>
        private bool REL()
        {
            bool isComplete = false;
            switch (cycles)
            {
                case 0:
                    clearInstrState();
                    break;
                case 1:
                    addr_rel = unchecked((sbyte)read(pc));
                    pc++;
                    instr_state_bytes[STATE_ADDR_MODE_COMPLETED_CYCLE] = cycles;
                    isComplete = true;
                    break;
                default:
                    isComplete = true;
                    break;
            }

            return isComplete;
        }

        /// <summary>
        /// Address Mode: Absolute
        /// </summary>
        /// <returns></returns>
        /// <remarks>
        /// A full 16-bit address is loaded and used
        /// </remarks>
        private bool ABS()
        {
            bool isComplete = false;
            switch (cycles)
            {
                case 0:
                    clearInstrState();
                    break;
                case 1:
                    instr_state_ushort["lo"] = read(pc);
                    pc++;
                    // JSR ends this part early
                    if (opcode_lookup[opcode].operation == JSR)
                    {
                        instr_state_bytes[STATE_ADDR_MODE_COMPLETED_CYCLE] = cycles;
                        isComplete = true;
                    }
                    break;
                case 2:
                    if (instr_state_bytes.ContainsKey(STATE_ADDR_MODE_COMPLETED_CYCLE))
                    {
                        isComplete = true;
                        break;
                    }

                    ushort hi = read(pc);
                    pc++;
                    addr_abs = (ushort)((hi << 8) | instr_state_ushort["lo"]);
                    if (opcode_lookup[opcode].instr_type == CPUInstructionType.Write ||
                        opcode_lookup[opcode].instr_type == CPUInstructionType.Branch)
                    {
                        instr_state_bytes[STATE_ADDR_MODE_COMPLETED_CYCLE] = cycles;
                        isComplete = true;
                    }
                    break;
                case 3:
                    if (instr_state_bytes.ContainsKey(STATE_ADDR_MODE_COMPLETED_CYCLE))
                    {
                        isComplete = true;
                        break;
                    }

                    if (opcode_lookup[opcode].instr_type == CPUInstructionType.R_M_W ||
                        opcode_lookup[opcode].instr_type == CPUInstructionType.Read)
                    {
                        fetch();
                        instr_state_bytes[STATE_ADDR_MODE_COMPLETED_CYCLE] = cycles;
                    }
                    else
                    {
                        Log.Error($"[{clock_count}] ABS addressing mode error - extra cycle executed when it shouldn't have!");
                    }
                    isComplete = true;
                    break;
                default:
                    isComplete = true;
                    break;
            }
            return isComplete;
        }

        /// <summary>
        /// Address Mode: Absolute with X Offset
        /// </summary>
        /// <returns></returns>
        /// <remarks>
        /// Fundamentally the same as absolute addressing, but the contents of the X Register
        /// is added to the supplied two byte address. If the resulting address changes
        /// the page, an additional clock cycle is required
        /// </remarks>
        private bool ABX()
        {
            bool isComplete = false;
            switch (cycles)
            {
                case 0:
                    clearInstrState();
                    break;
                case 1:
                    instr_state_ushort["lo"] = read(pc);
                    pc++;
                    break;
                case 2:
                    ushort hi = read(pc);
                    instr_state_ushort["hi"] = hi;
                    pc++;
                    addr_abs = (ushort)((hi << 8) | instr_state_ushort["lo"]);
                    instr_state_ushort["addr_eff"] = (ushort)((hi << 8) | (byte)(instr_state_ushort["lo"] + x));
                    break;
                case 3:
                    // Read without page boundary checking
                    fetched = read(instr_state_ushort["addr_eff"]);
                    addr_abs += x;
                    instr_state["page_cross"] = (addr_abs & 0xFF00) != ((instr_state_ushort["hi"]) << 8);
                    if ((bool)instr_state["page_cross"] ||
                        opcode_lookup[opcode].instr_type == CPUInstructionType.R_M_W ||
                        opcode_lookup[opcode].instr_type == CPUInstructionType.Write)
                    {
                        if (opcode_lookup[opcode].instr_type == CPUInstructionType.Read)
                        {
                            // If page boundary crossed, we need another cycle
                            _instCycleCount++;
                        }
                    }
                    else
                    {
                        // We don't need another cycle, end it here.
                        instr_state_bytes[STATE_ADDR_MODE_COMPLETED_CYCLE] = cycles;
                        isComplete = true;
                    }
                    break;
                case 4:
                    if (instr_state_bytes.ContainsKey(STATE_ADDR_MODE_COMPLETED_CYCLE))
                    {
                        isComplete = true;
                        break;
                    }

                    if ((bool)instr_state["page_cross"] ||
                        opcode_lookup[opcode].instr_type == CPUInstructionType.R_M_W ||
                        opcode_lookup[opcode].instr_type == CPUInstructionType.Write)
                    {
                        if (opcode_lookup[opcode].instr_type != CPUInstructionType.Write)
                        {
                            // Read again from correct address
                            fetched = read(addr_abs);
                        }
                        instr_state_bytes[STATE_ADDR_MODE_COMPLETED_CYCLE] = cycles;
                    }
                    else
                    {
                        Log.Error($"[{clock_count}] ABX cycle 4 executed when it shouldn't have!");
                    }
                    isComplete = true;
                    break;
                default:
                    isComplete = true;
                    break;
            }

            return isComplete;
        }

        /// <summary>
        /// Address Mode: Absolute with Y Offset
        /// </summary>
        /// <returns></returns>
        /// <remarks>
        /// Fundamentally the same as absolute addressing, but the contents of the Y Register
        /// is added to the supplied two byte address. If the resulting address changes
        /// the page, an additional clock cycle is required
        /// </remarks>
        private bool ABY()
        {
            bool isComplete = false;
            switch (cycles)
            {
                case 0:
                    clearInstrState();
                    break;
                case 1:
                    instr_state_ushort["lo"] = read(pc);
                    pc++;
                    break;
                case 2:
                    ushort hi = read(pc);
                    instr_state_ushort["hi"] = hi;
                    pc++;
                    addr_abs = (ushort)((hi << 8) | instr_state_ushort["lo"]);
                    instr_state_ushort["addr_eff"] = (ushort)((hi << 8) | (byte)(instr_state_ushort["lo"] + y));
                    break;
                case 3:
                    // read from effective address, before checking if we crossed page boundary
                    //Log.Debug($"[{clock_count}] ABY read effective address (cycle 3) [opcode={opcode:X2}] [addr_eff={(ushort)instr_state["addr_eff"]:X4}]"); 
                    fetched = read(instr_state_ushort["addr_eff"]);
                    // did we cross a page boundary?
                    addr_abs += y;
                    instr_state["page_cross"] = (addr_abs & 0xFF00) != ((instr_state_ushort["hi"]) << 8);
                    if ((bool)instr_state["page_cross"] ||
                        opcode_lookup[opcode].instr_type == CPUInstructionType.R_M_W ||
                        opcode_lookup[opcode].instr_type == CPUInstructionType.Write)
                    {
                        if (opcode_lookup[opcode].instr_type == CPUInstructionType.Read)
                        {
                            // If page boundary crossed, we need another cycle
                            _instCycleCount++;
                        }
                    }
                    else
                    {
                        // We don't need another cycle, end it here.
                        instr_state_bytes[STATE_ADDR_MODE_COMPLETED_CYCLE] = cycles;
                        isComplete = true;
                    }
                    break;
                case 4:
                    if (instr_state_bytes.ContainsKey(STATE_ADDR_MODE_COMPLETED_CYCLE))
                    {
                        isComplete = true;
                        break;
                    }

                    if ((bool)instr_state["page_cross"] ||
                        opcode_lookup[opcode].instr_type == CPUInstructionType.R_M_W ||
                        opcode_lookup[opcode].instr_type == CPUInstructionType.Write)
                    {
                        if (opcode_lookup[opcode].instr_type != CPUInstructionType.Write)
                        {
                            //Log.Debug($"[{clock_count}] ABY read after adjusting address (cycle 4) [opcode={opcode:X2}] [addr_abs={addr_abs:X4}]");
                            // Read again from correct address
                            fetched = read(addr_abs);
                        }
                        instr_state_bytes[STATE_ADDR_MODE_COMPLETED_CYCLE] = cycles;
                    }
                    else
                    {
                        Log.Error($"[{clock_count}] ABY cycle 4 executed when it shouldn't have!");
                    }
                    isComplete = true;
                    break;
                default:
                    isComplete = true;
                    break;
            }

            return isComplete;
        }

        /// <summary>
        /// Address Mode: Indirect
        /// </summary>
        /// <returns></returns>
        /// <remarks>
        /// This mode is only used by the JMP instruction. 
        /// The supplied 16-bit address is read to get the actual 16-bit address. This 
        /// instruction is unusual in that it has a bug in the hardware! To emulate its
        /// function accurately, we also need to emulate this bug. If the low byte of the
        /// supplied address is 0xFF, then to read the high byte of the actual address
        /// we need to cross a page boundary. This doesn't actually work on the chip as 
        /// designed, instead it wraps back around in the same page, yielding an 
        /// invalid actual address.
        /// </remarks>
        private bool IND()
        {
            bool isComplete = false;
            switch (cycles)
            {
                case 0:
                    clearInstrState();
                    break;
                case 1:
                    // Fetch pointer address low
                    instr_state_ushort["ptr_lo"] = read(pc);
                    pc++;
                    break;
                case 2:
                    // Fetch pointer address high
                    ushort hi = read(pc);
                    pc++;
                    instr_state_ushort["ptr"] = (ushort)((hi << 8) | instr_state_ushort["ptr_lo"]);
                    break;
                case 3:
                    instr_state_ushort["addr_lo"] = read(instr_state_ushort["ptr"]);
                    break;
                case 4:
                    ushort lo = instr_state_ushort["addr_lo"];
                    if (instr_state_ushort["ptr_lo"] == 0x00FF)
                    {
                        // Simulate page boundary hardware bug
                        addr_abs = (ushort)((read((ushort)(instr_state_ushort["ptr"] & 0xFF00)) << 8) | lo);
                    }
                    else
                    {
                        // Normal behavior
                        addr_abs = (ushort)((read((ushort)(instr_state_ushort["ptr"] + 1)) << 8) | lo);
                    }
                    pc = addr_abs;
                    instr_state_bytes[STATE_ADDR_MODE_COMPLETED_CYCLE] = cycles;
                    isComplete = true;
                    break;
                default:
                    isComplete = true;
                    break;
            }
            return isComplete;
        }

        /// <summary>
        /// Address Mode: Indirect X
        /// </summary>
        /// <returns></returns>
        /// <remarks>
        /// The supplied 8-bit address is offset by X Register to index a location in page 0x00. 
        /// The actual 16-bit address is read from this location. For some reason, page boundary 
        /// crossing is not checked for this mode (even though it is for IZY).
        /// </remarks>
        private bool IZX()
        {
            bool isComplete = false;
            switch (cycles)
            {
                case 0:
                    clearInstrState();
                    break;
                case 1:
                    // fetch pointer address, increment PC
                    instr_state_ushort["ptr"] = read(pc);
                    pc++;
                    break;
                case 2:
                    // Read from address
                    read(instr_state_ushort["ptr"]);
                    break;
                case 3:
                    // fetch effective address low
                    var ptr = (ushort)(instr_state_ushort["ptr"] + x);
                    instr_state_ushort["addr_eff_lo"] = read((ushort)(ptr & 0x00FF));
                    break;
                case 4:
                    var ptr2 = (ushort)(instr_state_ushort["ptr"] + x + 1);
                    // fetch effective address hi
                    ushort hi = read((ushort)(ptr2 & 0x00FF));
                    addr_abs = (ushort)((hi << 8) | instr_state_ushort["addr_eff_lo"]);
                    break;
                case 5:
                    if (opcode_lookup[opcode].instr_type != CPUInstructionType.Write)
                        fetch();
                    instr_state_bytes[STATE_ADDR_MODE_COMPLETED_CYCLE] = cycles;
                    isComplete = true;
                    break;
                default:
                    isComplete = true;
                    break;
            }

            return isComplete;
        }

        /// <summary>
        /// Address Mode: Indirect Y
        /// </summary>
        /// <returns></returns>
        /// <remarks>
        /// The supplied 8-bit address indexes a location in page 0x00. From here the actual 
        /// 16-bit address is read, and the contents of the Y Register is added to it to offset 
        /// it. If the offset causes a change in page then an additional clock cycle is required.
        /// </remarks>
        private bool IZY()
        {
            bool isComplete = false;
            switch (cycles)
            {
                case 0:
                    clearInstrState();
                    break;
                case 1:
                    // fetch pointer address, increment PC
                    instr_state_ushort["ptr"] = read(pc);
                    pc++;
                    break;
                case 2:
                    // Fetch effective address low
                    instr_state_bytes["addr_eff_lo"] = read(instr_state_ushort["ptr"]);
                    break;
                case 3:
                    // fetch effective address high
                    ushort hi = (ushort)(instr_state_ushort["ptr"] + 1);
                    instr_state_ushort["addr_eff_hi"] = read((ushort)(hi & 0x00FF));
                    addr_abs = (ushort)(instr_state_ushort["addr_eff_hi"] << 8 | instr_state_bytes["addr_eff_lo"]);
                    instr_state_bytes["addr_eff_lo"] = (byte)(instr_state_bytes["addr_eff_lo"] + y);
                    break;
                case 4:
                    // Read from effective address
                    var addr_eff = (ushort)(instr_state_ushort["addr_eff_hi"] << 8 | instr_state_bytes["addr_eff_lo"]);
                    //Log.Debug($"[{clock_count}] IZY read effective address (cycle 4) [opcode={opcode:X2}] [addr_eff={addr_eff:X4}]");
                    fetched = read(addr_eff);
                    // did we cross a page boundary?
                    instr_state["page_cross"] = (addr_abs & 0xFF00) != ((addr_abs + y) & 0xFF00);
                    if ((bool)instr_state["page_cross"] ||
                        opcode_lookup[opcode].instr_type == CPUInstructionType.R_M_W ||
                        opcode_lookup[opcode].instr_type == CPUInstructionType.Write)
                    {
                        if (opcode_lookup[opcode].instr_type == CPUInstructionType.Read)
                        {
                            //Log.Debug($"[{clock_count}] Adding cycle for page-crossed IZY instruction");
                            // Add a cycle to fix address and read again
                            _instCycleCount++;
                        }
                        addr_abs += y;
                    }
                    else
                    {
                        // address did not cross page boundary
                        addr_abs = addr_eff;
                        instr_state_bytes[STATE_ADDR_MODE_COMPLETED_CYCLE] = cycles;
                        isComplete = true;
                    }
                    break;
                case 5:
                    if (instr_state_bytes.ContainsKey(STATE_ADDR_MODE_COMPLETED_CYCLE))
                    {
                        isComplete = true;
                        break;
                    }

                    if ((bool)instr_state["page_cross"] ||
                        opcode_lookup[opcode].instr_type == CPUInstructionType.R_M_W ||
                        opcode_lookup[opcode].instr_type == CPUInstructionType.Write)
                    {
                        if (opcode_lookup[opcode].instr_type != CPUInstructionType.Write)
                        {
                            //Log.Debug($"[{clock_count}] Reading after page-crossed IZY instruction (cycle 5) [opcode={opcode:X2}] [addr_abs={addr_abs:X4}]");
                            fetched = read(addr_abs);
                        }
                        instr_state_bytes[STATE_ADDR_MODE_COMPLETED_CYCLE] = cycles;
                        isComplete = true;
                    }
                    else
                    {
                        Log.Error($"[{clock_count}] IZY addressing mode error - cycle 5 executed when it shouldn't have!");
                    }
                    break;
                default:
                    isComplete = true;
                    break;
            }
            return isComplete;
        }

        #endregion // Addressing Modes

        #region OpCodes
        /*****
         * There are 56 "legitimate" opcodes provided by the 6502 CPU. I have not modelled "unofficial" opcodes. As each opcode is 
         * defined by 1 byte, there are potentially 256 possible codes. Codes are not used in a "switch case" style on a processor,
         * instead they are repsonisble for switching individual parts of CPU circuits on and off. The opcodes listed here are official, 
         * meaning that the functionality of the chip when provided with these codes is as the developers intended it to be. Unofficial
         * codes will of course also influence the CPU circuitry in interesting ways, and can be exploited to gain additional
         * functionality!
         * 
         * These functions return 0 normally, but some are capable of requiring more clock cycles when executed under certain
         * conditions combined with certain addressing modes. If that is the case, they return 1.
         *****/

        #region Arithmetic instructions
        /// <summary>
        /// Instruction: Add with Carry In
        /// </summary>
        /// <returns></returns>
        /// <remarks>
        /// Function:    A = A + M + C
        /// Flags Out:   C, V, N, Z
        /// </remarks>
        private byte ADC()
        {
            // Which cycle did the addressing mode operations end?
            byte opcodeStartCycle = instr_state_bytes[STATE_ADDR_MODE_COMPLETED_CYCLE];

            // This is a read instruction, so everything should be done on the last cycle.
            if (cycles == opcodeStartCycle)
            {
                // Add is performed in 16-bit domain for emulation to capture any
                // carry bit, which will exist in bit 8 of the 16-bit word
                temp = (ushort)(a + fetched + getFlag(FLAGS6502.C));

                // The carry flag out exists in the high byte bit 0
                setFlag(FLAGS6502.C, temp > 255);

                // The Zero flag is set if the result is 0
                testAndSet(FLAGS6502.Z, temp);

                // The signed Overflow flag is set based on all that up there! :D
                setFlag(FLAGS6502.V, ((~(a ^ fetched) & (a ^ temp)) & 0x0080) != 0);

                // The negative flag is set to the most significant bit of the result
                testAndSet(FLAGS6502.N, temp);

                // Load the result into the accumulator (it's 8-bit dont forget!)
                a = (byte)(temp & 0x00FF);
            }
            else
            {
                Log.Error($"[{clock_count}] ADC error - incorrect cycle! [cycles={cycles}] [opcodeStartCycle={opcodeStartCycle}] [_instCycleCount={_instCycleCount}]");
            }
            // This instruction has the potential to require an additional clock cycle
            // (we don't use this anymore)
            return 1;
        }

        /// <summary>
        /// Instruction: Subtraction with Borrow In
        /// Function:    A = A - M - (1 - C)
        /// Flags Out:   C, V, N, Z
        /// </summary>
        /// <returns></returns>
        private byte SBC()
        {
            // Which cycle did the addressing mode operations end?
            byte opcodeStartCycle = instr_state_bytes[STATE_ADDR_MODE_COMPLETED_CYCLE];

            // This is a read instruction, so everything should be done on the last cycle.
            if (cycles == opcodeStartCycle)
            {
                // Operating in 16-bit domain to capture carry out

                // We can invert the bottom 8 bits with bitwise xor
                ushort value = (ushort)((fetched) ^ 0x00FF);

                // Notice this is exactly the same as addition from here!
                temp = (ushort)(a + value + getFlag(FLAGS6502.C));
                setFlag(FLAGS6502.C, (temp & 0xFF00) != 0);
                testAndSet(FLAGS6502.Z, temp);
                setFlag(FLAGS6502.V, ((temp ^ a) & (temp ^ value) & 0x0080) != 0);
                testAndSet(FLAGS6502.N, temp);
                a = (byte)(temp & 0x00FF);
            }
            else
            {
                Log.Error($"[{clock_count}] SBC error - incorrect cycle! [cycles={cycles}] [opcodeStartCycle={opcodeStartCycle}] [_instCycleCount={_instCycleCount}]");
            }
            // This instruction has the potential to require an additional clock cycle
            // (we don't use this anymore)
            return 1;
        }

        #endregion // Arithmetic instructions

        #region Bitwise Operators
        /// <summary>
        /// Instruction: Bitwise Logic AND
        /// </summary>
        /// <returns></returns>
        /// <remarks>
        /// Function:    A = A & M
        /// Flags Out:   N, Z
        /// </remarks>
        private byte AND()
        {
            // Which cycle did the addressing mode operations end?
            byte opcodeStartCycle = instr_state_bytes[STATE_ADDR_MODE_COMPLETED_CYCLE];

            // This is a read instruction, so everything should be done on the last cycle.
            if (cycles == opcodeStartCycle)
            {
                a = (byte)(a & fetched);
                testAndSet(FLAGS6502.Z, a);
                testAndSet(FLAGS6502.N, a);
            }
            else
            {
                Log.Error($"[{clock_count}] AND error - incorrect cycle! [cycles={cycles}] [opcodeStartCycle={opcodeStartCycle}] [_instCycleCount={_instCycleCount}]");
            }
            // This instruction has the potential to require an additional clock cycle
            // (we don't use this anymore)
            return 1;
        }

        /// <summary>
        /// Instruction: Arithmetic Shift Left
        /// Function:    A = C <- (A << 1) <- 0
        /// Flags Out:   N, Z, C
        /// </summary>
        /// <returns></returns>
        private byte ASL()
        {
            // Which cycle did the addressing mode operations end?
            byte opcodeStartCycle = instr_state_bytes[STATE_ADDR_MODE_COMPLETED_CYCLE];

            if (cycles == opcodeStartCycle + 1)
            {
                if (opcode_lookup[opcode].addr_mode != IMP)
                {
                    // Write the value back to the effective address, and do the operation on it
                    write(addr_abs, fetched);
                }
                temp = (ushort)(fetched << 1);
                setFlag(FLAGS6502.C, (temp & 0xFF00) > 0);
                testAndSet(FLAGS6502.Z, temp);
                testAndSet(FLAGS6502.N, temp);

                if (opcode_lookup[opcode].addr_mode == IMP)
                {
                    a = (byte)(temp & 0x00FF);
                }
            }
            else if (cycles == opcodeStartCycle + 2)
            {
                // write the new value to the effective address
                write(addr_abs, (byte)(temp & 0x00FF));
            }
            return 0;
        }

        /// <summary>
        /// Instruction: Shift one bit right
        /// Function:    A = 0 -> (A >> 1) -> C
        /// Flags Out:   C, Z, N
        /// </summary>
        /// <returns></returns>
        private byte LSR()
        {
            // Which cycle did the addressing mode operations end?
            byte opcodeStartCycle = instr_state_bytes[STATE_ADDR_MODE_COMPLETED_CYCLE];

            if (cycles == opcodeStartCycle + 1)
            {
                if (opcode_lookup[opcode].addr_mode != IMP)
                {
                    // Write the value back to the effective address, and do the operation on it
                    write(addr_abs, fetched);
                }
                setFlag(FLAGS6502.C, (fetched & 0x0001) == 1);
                temp = (ushort)(fetched >> 1);
                testAndSet(FLAGS6502.Z, temp);
                testAndSet(FLAGS6502.N, temp);

                if (opcode_lookup[opcode].addr_mode == IMP)
                {
                    a = (byte)(temp & 0x00FF);
                }
            }
            else if (cycles == opcodeStartCycle + 2)
            {
                // write the new value to the effective address
                write(addr_abs, (byte)(temp & 0x00FF));
            }
            return 0;
        }

        /// <summary>
        /// Instruction: Rotate One bit Left
        /// Function:    A or M = C <- (M << 1) <- C
        /// Flags Out:   C, Z, N
        /// </summary>
        /// <returns></returns>
        private byte ROL()
        {
            // Which cycle did the addressing mode operations end?
            byte opcodeStartCycle = instr_state_bytes[STATE_ADDR_MODE_COMPLETED_CYCLE];

            if (cycles == opcodeStartCycle + 1)
            {
                if (opcode_lookup[opcode].addr_mode != IMP)
                {
                    // Write the value back to the effective address, and do the operation on it
                    write(addr_abs, fetched);
                }
                temp = (ushort)((fetched << 1) | getFlag(FLAGS6502.C));
                setFlag(FLAGS6502.C, (temp & 0xFF00) > 0);
                testAndSet(FLAGS6502.Z, temp);
                testAndSet(FLAGS6502.N, temp);

                if (opcode_lookup[opcode].addr_mode == IMP)
                {
                    a = (byte)(temp & 0x00FF);
                }
            }
            else if (cycles == opcodeStartCycle + 2)
            {
                // write the new value to the effective address
                write(addr_abs, (byte)(temp & 0x00FF));
            }
            return 0;
        }

        /// <summary>
        /// Instruction: Rotate One bit Right
        /// Function:    A or M = C -> (M >> 1) -> C
        /// Flags Out:   C, Z, N
        /// </summary>
        /// <returns></returns>
        private byte ROR()
        {
            // Which cycle did the addressing mode operations end?
            byte opcodeStartCycle = instr_state_bytes[STATE_ADDR_MODE_COMPLETED_CYCLE];

            if (cycles == opcodeStartCycle + 1)
            {
                if (opcode_lookup[opcode].addr_mode != IMP)
                {
                    // Write the value back to the effective address, and do the operation on it
                    write(addr_abs, fetched);
                }
                temp = (ushort)((getFlag(FLAGS6502.C) << 7) | fetched >> 1);
                setFlag(FLAGS6502.C, (fetched & 0x01) == 1);
                testAndSet(FLAGS6502.Z, temp);
                testAndSet(FLAGS6502.N, temp);

                if (opcode_lookup[opcode].addr_mode == IMP)
                {
                    a = (byte)(temp & 0x00FF);
                }
            }
            else if (cycles == opcodeStartCycle + 2)
            {
                if (opcode_lookup[opcode].addr_mode == IMP)
                {
                    Log.Error($"[{clock_count}] ROR error! Executing cycle {cycles} in IMP mode!");
                }
                // write the new value to the effective address
                write(addr_abs, (byte)(temp & 0x00FF));
            }
            return 0;
        }

        /// <summary>
        /// Instruction: Test memory bits with accumulator
        /// Flags Out:   Z, N, V
        /// </summary>
        /// <returns></returns>
        private byte BIT()
        {
            // Which cycle did the addressing mode operations end?
            byte opcodeStartCycle = instr_state_bytes[STATE_ADDR_MODE_COMPLETED_CYCLE];

            // This is a read instruction, so everything should be done on the last cycle.
            if (cycles == opcodeStartCycle)
            {
                temp = (ushort)(a & fetched);
                testAndSet(FLAGS6502.Z, temp);
                testAndSet(FLAGS6502.N, fetched);
                setFlag(FLAGS6502.V, (fetched & (1 << 6)) != 0);
            }
            else
            {
                Log.Error($"[{clock_count}] BIT error - incorrect cycle! [cycles={cycles}] [opcodeStartCycle={opcodeStartCycle}] [_instCycleCount={_instCycleCount}]");
            }
            return 0;
        }

        /// <summary>
        /// Instruction: Bitwise Logic XOR
        /// Function:    A = A xor M
        /// Flags Out:   N, Z
        /// </summary>
        /// <returns></returns>
        private byte EOR()
        {
            // Which cycle did the addressing mode operations end?
            byte opcodeStartCycle = instr_state_bytes[STATE_ADDR_MODE_COMPLETED_CYCLE];

            // This is a read instruction, so everything should be done on the last cycle.
            if (cycles == opcodeStartCycle)
            {
                a = (byte)(a ^ fetched);
                testAndSet(FLAGS6502.Z, a);
                testAndSet(FLAGS6502.N, a);
            }
            else
            {
                Log.Error($"[{clock_count}] EOR error - incorrect cycle! [cycles={cycles}] [opcodeStartCycle={opcodeStartCycle}] [_instCycleCount={_instCycleCount}]");
            }
            return 1;
        }

        /// <summary>
        /// Instruction: Bitwise Logic OR
        /// </summary>
        /// <returns></returns>
        /// <remarks>
        /// Function:    A = A | M
        /// Flags Out:   N, Z
        /// </remarks>
        private byte ORA()
        {
            // Which cycle did the addressing mode operations end?
            byte opcodeStartCycle = instr_state_bytes[STATE_ADDR_MODE_COMPLETED_CYCLE];

            // This is a read instruction, so everything should be done on the last cycle.
            if (cycles == opcodeStartCycle)
            {
                a |= fetched;
                testAndSet(FLAGS6502.Z, a);
                testAndSet(FLAGS6502.N, a);
            }
            else
            {
                Log.Error($"[{clock_count}] ORA error - incorrect cycle! [cycles={cycles}] [opcodeStartCycle={opcodeStartCycle}] [_instCycleCount={_instCycleCount}]");
            }
            return 1;
        }
        #endregion // Bitwise Operators

        #region Branch instructions

        /// <summary>
        /// Instruction: Branch if Carry Clear
        /// Function:    if(C == 0) pc = address 
        /// </summary>
        /// <returns></returns>
        private byte BCC()
        {
            // Which cycle did the addressing mode operations end?
            byte opcodeStartCycle = instr_state_bytes[STATE_ADDR_MODE_COMPLETED_CYCLE];

            if (cycles == opcodeStartCycle)
            {
                if (getFlag(FLAGS6502.C) == 0)
                {
                    // "If branch is taken, add operand to PCL."
                    addr_abs = (ushort)(pc + addr_rel);
                    instr_state["need_extra"] = 1;
                    _instCycleCount++;
                }
            }
            else if (cycles == opcodeStartCycle + 1)
            {
                // sanity check
                if (!instr_state.ContainsKey("need_extra"))
                {
                    Log.Error($"[{clock_count}] BCC error - extra cycle executed when not needed!");
                }
                // "Fix PCH."
                if ((addr_abs & 0xFF00) != (pc & 0xFF00))
                {
                    instr_state["need_extra2"] = 1;
                    _instCycleCount++;
                }
                pc = addr_abs;
            }

            return 0;
        }

        /// <summary>
        /// Instruction: Branch if Carry Set
        /// Function:    if(C == 1) pc = address
        /// </summary>
        /// <returns></returns>
        private byte BCS()
        {
            // Which cycle did the addressing mode operations end?
            byte opcodeStartCycle = instr_state_bytes[STATE_ADDR_MODE_COMPLETED_CYCLE];

            if (cycles == opcodeStartCycle)
            {
                if (getFlag(FLAGS6502.C) == 1)
                {
                    // "If branch is taken, add operand to PCL."
                    addr_abs = (ushort)(pc + addr_rel);
                    instr_state["need_extra"] = 1;
                    _instCycleCount++;
                }
            }
            else if (cycles == opcodeStartCycle + 1)
            {
                // sanity check
                if (!instr_state.ContainsKey("need_extra"))
                {
                    Log.Error($"[{clock_count}] BCS error - extra cycle executed when not needed!");
                }
                // "Fix PCH."
                if ((addr_abs & 0xFF00) != (pc & 0xFF00))
                {
                    instr_state["need_extra2"] = 1;
                    _instCycleCount++;
                }
                pc = addr_abs;
            }
            return 0;
        }

        /// <summary>
        /// Instruction: Branch if Equal
        /// Function:    if(Z == 1) pc = address
        /// </summary>
        /// <returns></returns>
        private byte BEQ()
        {
            // Which cycle did the addressing mode operations end?
            byte opcodeStartCycle = instr_state_bytes[STATE_ADDR_MODE_COMPLETED_CYCLE];

            if (cycles == opcodeStartCycle)
            {
                // "If branch is taken, add operand to PCL."
                if (getFlag(FLAGS6502.Z) == 1)
                {
                    addr_abs = (ushort)(pc + addr_rel);
                    instr_state["need_extra"] = 1;
                    _instCycleCount++;
                }
            }
            else if (cycles == opcodeStartCycle + 1)
            {
                // sanity check
                if (!instr_state.ContainsKey("need_extra"))
                {
                    Log.Error($"[{clock_count}] BEQ error - extra cycle executed when not needed!");
                }
                // "Fix PCH."
                if ((addr_abs & 0xFF00) != (pc & 0xFF00))
                {
                    instr_state["need_extra2"] = 1;
                    _instCycleCount++;
                }
                pc = addr_abs;
            }
            return 0;
        }

        /// <summary>
        /// Instruction: Branch if Negative
        /// Function:    if(N == 1) pc = address
        /// </summary>
        /// <returns></returns>
        private byte BMI()
        {
            // Which cycle did the addressing mode operations end?
            byte opcodeStartCycle = instr_state_bytes[STATE_ADDR_MODE_COMPLETED_CYCLE];

            if (cycles == opcodeStartCycle)
            {
                // "If branch is taken, add operand to PCL."
                if (getFlag(FLAGS6502.N) == 1)
                {
                    addr_abs = (ushort)(pc + addr_rel);
                    instr_state["need_extra"] = 1;
                    _instCycleCount++;
                }
            }
            else if (cycles == opcodeStartCycle + 1)
            {
                // sanity check
                if (!instr_state.ContainsKey("need_extra"))
                {
                    Log.Error($"[{clock_count}] BMI error - extra cycle executed when not needed!");
                }
                // "Fix PCH."
                if ((addr_abs & 0xFF00) != (pc & 0xFF00))
                {
                    instr_state["need_extra2"] = 1;
                    _instCycleCount++;
                }
                pc = addr_abs;
            }
            return 0;
        }

        /// <summary>
        /// Instruction: Branch if Not Equal
        /// Function:    if(Z == 0) pc = address
        /// </summary>
        /// <returns></returns>
        private byte BNE()
        {
            // Which cycle did the addressing mode operations end?
            byte opcodeStartCycle = instr_state_bytes[STATE_ADDR_MODE_COMPLETED_CYCLE];

            if (cycles == opcodeStartCycle)
            {
                // "If branch is taken, add operand to PCL."
                if (getFlag(FLAGS6502.Z) == 0)
                {
                    addr_abs = (ushort)(pc + addr_rel);
                    instr_state["need_extra"] = 1;
                    _instCycleCount++;
                }
            }
            else if (cycles == opcodeStartCycle + 1)
            {
                // sanity check
                if (!instr_state.ContainsKey("need_extra"))
                {
                    Log.Error($"[{clock_count}] BNE error - extra cycle executed when not needed!");
                }
                // "Fix PCH."
                if ((addr_abs & 0xFF00) != (pc & 0xFF00))
                {
                    _instCycleCount++;
                }
                pc = addr_abs;
            }
            return 0;
        }

        /// <summary>
        /// Instruction: Branch if Positive
        /// Function:    if(N == 0) pc = address
        /// </summary>
        /// <returns></returns>
        private byte BPL()
        {
            // Which cycle did the addressing mode operations end?
            byte opcodeStartCycle = instr_state_bytes[STATE_ADDR_MODE_COMPLETED_CYCLE];

            if (cycles == opcodeStartCycle)
            {
                // "If branch is taken, add operand to PCL."
                if (getFlag(FLAGS6502.N) == 0)
                {
                    addr_abs = (ushort)(pc + addr_rel);
                    instr_state["need_extra"] = 1;
                    _instCycleCount++;
                }
            }
            else if (cycles == opcodeStartCycle + 1)
            {
                // sanity check
                if (!instr_state.ContainsKey("need_extra"))
                {
                    Log.Error($"[{clock_count}] BPL error - extra cycle executed when not needed!");
                }
                // "Fix PCH."
                if ((addr_abs & 0xFF00) != (pc & 0xFF00))
                {
                    instr_state["need_extra2"] = 1;
                    _instCycleCount++;
                }
                pc = addr_abs;
            }
            return 0;
        }

        /// <summary>
        /// Instruction: Branch if Overflow Clear
        /// Function:    if(V == 0) pc = address
        /// </summary>
        /// <returns></returns>
        private byte BVC()
        {
            // Which cycle did the addressing mode operations end?
            byte opcodeStartCycle = instr_state_bytes[STATE_ADDR_MODE_COMPLETED_CYCLE];

            if (cycles == opcodeStartCycle)
            {
                // "If branch is taken, add operand to PCL."
                if (getFlag(FLAGS6502.V) == 0)
                {
                    addr_abs = (ushort)(pc + addr_rel);
                    instr_state["need_extra"] = 1;
                    _instCycleCount++;
                }
            }
            else if (cycles == opcodeStartCycle + 1)
            {
                // sanity check
                if (!instr_state.ContainsKey("need_extra"))
                {
                    Log.Error($"[{clock_count}] BVC error - extra cycle executed when not needed!");
                }
                // "Fix PCH."
                if ((addr_abs & 0xFF00) != (pc & 0xFF00))
                {
                    instr_state["need_extra2"] = 1;
                    _instCycleCount++;
                }
                pc = addr_abs;
            }
            return 0;
        }

        /// <summary>
        /// Instruction: Branch if Overflow Set
        /// Function:    if(V == 1) pc = address
        /// </summary>
        /// <returns></returns>
        private byte BVS()
        {
            // Which cycle did the addressing mode operations end?
            byte opcodeStartCycle = instr_state_bytes[STATE_ADDR_MODE_COMPLETED_CYCLE];

            if (cycles == opcodeStartCycle)
            {
                // "If branch is taken, add operand to PCL."
                if (getFlag(FLAGS6502.V) == 1)
                {
                    addr_abs = (ushort)(pc + addr_rel);
                    instr_state["need_extra"] = 1;
                    _instCycleCount++;
                }
            }
            else if (cycles == opcodeStartCycle + 1)
            {
                // sanity check
                if (!instr_state.ContainsKey("need_extra"))
                {
                    Log.Error($"[{clock_count}] BVS error - extra cycle executed when not needed!");
                }
                // "Fix PCH."
                if ((addr_abs & 0xFF00) != (pc & 0xFF00))
                {
                    instr_state["need_extra2"] = 1;
                    _instCycleCount++;
                }
                pc = addr_abs;
            }
            return 0;
        }

        #endregion // Branch instructions

        /// <summary>
        /// Instruction: Break
        /// Function:    Program Sourced Interrupt
        /// </summary>
        /// <returns></returns>
        private byte BRK()
        {
            // Which cycle did the addressing mode operations end?
            byte opcodeStartCycle = instr_state_bytes[STATE_ADDR_MODE_COMPLETED_CYCLE];
            if (cycles == opcodeStartCycle)
            {
                _irqVector = ADDR_IRQ;
                if (_nmiPending)
                {
                    Log.Debug($"[{clock_count}] IRQ/BRK has been hijacked by NMI!");
                    _irqVector = ADDR_NMI;
                }
            }
            else if (cycles == opcodeStartCycle + 1)
            {
                push((byte)((pc >> 8) & 0x00FF));
                if (_nmiPending)
                {
                    Log.Debug($"[{clock_count}] IRQ/BRK has been hijacked by NMI!");
                    _irqVector = ADDR_NMI;
                }
            }
            else if (cycles == opcodeStartCycle + 2)
            {
                push((byte)(pc & 0x00FF));
                if (_nmiPending)
                {
                    Log.Debug($"[{clock_count}] IRQ/BRK has been hijacked by NMI!");
                    _irqVector = ADDR_NMI;
                }
            }
            else if (cycles == opcodeStartCycle + 3)
            {
                setFlag(FLAGS6502.B, true);
                push((byte)status);
                setFlag(FLAGS6502.B, false);
                setFlag(FLAGS6502.I, true);
                if (_nmiPending == true)
                {
                    // NMI hijacked BRK
                    Log.Debug($"[{clock_count}] IRQ/BRK has been hijacked by NMI!");
                    _irqVector = ADDR_NMI;
                }
            }
            else if (cycles == opcodeStartCycle + 4)
            {
                instr_state_bytes["lo"] = read(_irqVector);
            }
            else if (cycles == opcodeStartCycle + 5)
            {
                byte hi = read((ushort)(_irqVector + 1));
                pc = (ushort)((hi << 8) | instr_state_bytes["lo"]);
            }
            return 0;
        }

        #region Clear instructions

        /// <summary>
        /// Instruction: Clear Carry Flag
        /// Function:    C = 0
        /// </summary>
        /// <returns></returns>
        private byte CLC()
        {
            if (cycles == 1)
            {
                // Dummy read
                read(pc);
                setFlag(FLAGS6502.C, false);
            }
            return 0;
        }

        /// <summary>
        /// Instruction: Clear Decimal Flag
        /// Function:    D = 0
        /// </summary>
        /// <returns></returns>
        private byte CLD()
        {
            if (cycles == 1)
            {
                // Dummy read
                read(pc);
                setFlag(FLAGS6502.D, false);
            }
            return 0;
        }

        /// <summary>
        /// Instruction: Clear Overflow Flag
        /// Function:    V = 0
        /// </summary>
        /// <returns></returns>
        private byte CLV()
        {
            if (cycles == 1)
            {
                // Dummy read
                read(pc);
                setFlag(FLAGS6502.V, false);
            }
            return 0;
        }

        /// <summary>
        /// Instruction: Enable Interrupts / Clear Interrupt Disable Flag
        /// Function:    I = 0
        /// </summary>
        /// <returns></returns>
        private byte CLI()
        {
            if (cycles == 1)
            {
                // Dummy read
                read(pc);
                //Log.Debug($"[{clock_count}] CLI");
                _startCountingIRQs = true;
                _irqCount = 0;
                if (getFlag(FLAGS6502.I) == 1)
                {
                    _irqEnableLatency = 1;
                }
                setFlag(FLAGS6502.I, false);
            }
            return 0;
        }
        #endregion // Clear instructions

        #region Set Instructions
        /// <summary>
        /// Instruction: Set Carry Flag
        /// Function:    C = 1
        /// Flags Out:   C
        /// </summary>
        /// <returns></returns>
        private byte SEC()
        {
            if (cycles == 1)
            {
                // Dummy read
                read(pc);
                setFlag(FLAGS6502.C, true);
            }
            return 0;
        }

        /// <summary>
        /// Instruction: Set Decimal Flag
        /// Function:    D = 1
        /// Flags Out:   D
        /// </summary>
        /// <returns></returns>
        private byte SED()
        {
            if (cycles == 1)
            {
                // Dummy read
                read(pc);
                setFlag(FLAGS6502.D, true);
            }
            return 0;
        }

        /// <summary>
        /// Instruction: Set Interrupt Disable Flag / Disable Interrupts
        /// Function:    I = 1
        /// Flags Out:   I
        /// </summary>
        /// <returns></returns>
        private byte SEI()
        {
            if (cycles == 1)
            {
                // Dummy read
                read(pc);
                if (getFlag(FLAGS6502.I) == 0)
                {
                    _irqDisablePending = true;
                    _startCountingIRQs = false;
                }

                setFlag(FLAGS6502.I, true);
            }
            return 0;
        }
        #endregion // Set Instructions

        #region Compare Instructions
        /// <summary>
        /// Instruction: Compare Accumulator
        /// Function:    C <- A >= M      Z <- (A - M) == 0
        /// Flags Out:   N, C, Z
        /// </summary>
        /// <returns></returns>
        private byte CMP()
        {
            // Which cycle did the addressing mode operations end?
            byte opcodeStartCycle = instr_state_bytes[STATE_ADDR_MODE_COMPLETED_CYCLE];

            // This is a read instruction, so everything should be done on the last cycle.
            if (cycles == opcodeStartCycle)
            {
                temp = (ushort)(a - fetched);
                setFlag(FLAGS6502.C, a >= fetched);
                testAndSet(FLAGS6502.Z, temp);
                testAndSet(FLAGS6502.N, temp);
            }
            return 1;
        }

        /// <summary>
        /// Instruction: Compare X Register
        /// Function:    C <- X >= M      Z <- (X - M) == 0
        /// Flags Out:   N, C, Z
        /// </summary>
        /// <returns></returns>
        private byte CPX()
        {
            // Which cycle did the addressing mode operations end?
            byte opcodeStartCycle = instr_state_bytes[STATE_ADDR_MODE_COMPLETED_CYCLE];

            // This is a read instruction, so everything should be done on the last cycle.
            if (cycles == opcodeStartCycle)
            {
                temp = (ushort)(x - fetched);
                setFlag(FLAGS6502.C, x >= fetched);
                testAndSet(FLAGS6502.Z, temp);
                testAndSet(FLAGS6502.N, temp);
            }
            return 0;
        }

        /// <summary>
        /// Instruction: Compare Y Register
        /// Function:    C <- Y >= M      Z <- (Y - M) == 0
        /// Flags Out:   N, C, Z
        /// </summary>
        /// <returns></returns>
        private byte CPY()
        {
            // Which cycle did the addressing mode operations end?
            byte opcodeStartCycle = instr_state_bytes[STATE_ADDR_MODE_COMPLETED_CYCLE];

            // This is a read instruction, so everything should be done on the last cycle.
            if (cycles == opcodeStartCycle)
            {
                temp = (ushort)(y - fetched);
                setFlag(FLAGS6502.C, y >= fetched);
                testAndSet(FLAGS6502.Z, temp);
                testAndSet(FLAGS6502.N, temp);
            }
            return 0;
        }
        #endregion // Compare Instructions

        /// <summary>
        /// Instruction: Decrement Value at Memory Location
        /// Function:    M = M - 1
        /// Flags Out:   N, Z
        /// </summary>
        /// <returns></returns>
        private byte DEC()
        {
            // Which cycle did the addressing mode operations end?
            byte opcodeStartCycle = instr_state_bytes[STATE_ADDR_MODE_COMPLETED_CYCLE];

            if (cycles == opcodeStartCycle + 1)
            {
                // Write the value back to the effective address, and do the operation on it
                write(addr_abs, fetched);
                temp = (ushort)(fetched - 1);
                testAndSet(FLAGS6502.Z, temp);
                testAndSet(FLAGS6502.N, temp);
            }
            else if (cycles == opcodeStartCycle + 2)
            {
                // write the new value to the effective address
                write(addr_abs, (byte)(temp & 0x00FF));
            }
            return 0;
        }

        /// <summary>
        /// Instruction: Decrement X Register
        /// Function:    X = X - 1
        /// Flags Out:   N, Z
        /// </summary>
        /// <returns></returns>
        private byte DEX()
        {
            if (cycles == 1)
            {
                x--;
                testAndSet(FLAGS6502.Z, x);
                testAndSet(FLAGS6502.N, x);
            }
            return 0;
        }

        /// <summary>
        /// Instruction: Decrement Y Register
        /// Function:    Y = Y - 1
        /// Flags Out:   N, Z
        /// </summary>
        /// <returns></returns>
        private byte DEY()
        {
            if (cycles == 1)
            {
                y--;
                testAndSet(FLAGS6502.Z, y);
                testAndSet(FLAGS6502.N, y);
            }
            return 0;
        }

        /// <summary>
        /// Instruction: Increment Value at Memory Location
        /// Function:    M = M + 1
        /// Flags Out:   N, Z
        /// </summary>
        /// <returns></returns>
        private byte INC()
        {
            // Which cycle did the addressing mode operations end?
            byte opcodeStartCycle = instr_state_bytes[STATE_ADDR_MODE_COMPLETED_CYCLE];

            if (cycles == opcodeStartCycle + 1)
            {
                // Write the value back to the effective address, and do the operation on it
                write(addr_abs, fetched);
                temp = (ushort)(fetched + 1);
                testAndSet(FLAGS6502.Z, temp);
                testAndSet(FLAGS6502.N, temp);
            }
            else if (cycles == opcodeStartCycle + 2)
            {
                write(addr_abs, (byte)(temp & 0x00FF));
            }
            return 0;
        }

        /// <summary>
        /// Instruction: Increment X Register
        /// Function:    X = X + 1
        /// Flags Out:   N, Z
        /// </summary>
        /// <returns></returns>
        private byte INX()
        {
            if (cycles == 1)
            {
                x++;
                testAndSet(FLAGS6502.Z, x);
                testAndSet(FLAGS6502.N, x);
            }
            return 0;
        }

        /// <summary>
        /// Instruction: Increment Y Register
        /// Function:    Y = Y + 1
        /// Flags Out:   N, Z
        /// </summary>
        /// <returns></returns>
        private byte INY()
        {
            if (cycles == 1)
            {
                y++;
                testAndSet(FLAGS6502.Z, y);
                testAndSet(FLAGS6502.N, y);
            }
            return 0;
        }

        /// <summary>
        /// Instruction: Jump To Location
        /// Function:    pc = address
        /// </summary>
        /// <returns></returns>
        private byte JMP()
        {
            // Depending on addressing mode, there's two possibilities here.
            if (opcode_lookup[opcode].addr_mode == ABS)
            {
                if (cycles == 2)
                {
                    pc = addr_abs;
                }
            }
            else if (opcode_lookup[opcode].addr_mode == IND)
            {
                // Indirect addressing mode is only for JMP, so we don't do anything here
                // since it is all handled in the IND cycles (including setting the PC)
            }
            return 0;
        }

        /// <summary>
        /// Instruction: Jump To Sub-Routine
        /// Function:    PC -> stack, pc = address
        /// </summary>
        /// <returns></returns>
        private byte JSR()
        {
            // Which cycle did the addressing mode operations end?
            //byte opcodeStartCycle = (byte)instr_state[STATE_ADDR_MODE_COMPLETED_CYCLE];

            if (cycles == 3)
            {
                push((byte)(pc >> 8));
            }
            else if (cycles == 4)
            {
                push((byte)(pc & 0x00FF));
            }
            else if (cycles == 5)
            {
                byte hi = read(pc);
                addr_abs = (ushort)((hi << 8) | instr_state_ushort["lo"]);
                pc = addr_abs;
            }

            return 0;
        }

        #region Load instructions

        /// <summary>
        /// Instruction: Load The Accumulator
        /// Function:    A = M
        /// Flags Out:   N, Z
        /// </summary>
        /// <returns></returns>
        private byte LDA()
        {
            // Which cycle did the addressing mode operations end?
            byte opcodeStartCycle = instr_state_bytes[STATE_ADDR_MODE_COMPLETED_CYCLE];

            // This is a read instruction, so everything should be done on the last cycle.
            if (cycles == opcodeStartCycle)
            {
                a = fetched;
                testAndSet(FLAGS6502.Z, a);
                testAndSet(FLAGS6502.N, a);
            }
            return 1;
        }

        /// <summary>
        /// Instruction: Load The X Register
        /// Function:    X = M
        /// Flags Out:   N, Z
        /// </summary>
        /// <returns></returns>
        private byte LDX()
        {
            // Which cycle did the addressing mode operations end?
            byte opcodeStartCycle = instr_state_bytes[STATE_ADDR_MODE_COMPLETED_CYCLE];

            // This is a read instruction, so everything should be done on the last cycle.
            if (cycles == opcodeStartCycle)
            {
                x = fetched;
                testAndSet(FLAGS6502.Z, x);
                testAndSet(FLAGS6502.N, x);
            }
            return 1;
        }

        /// <summary>
        /// Instruction: Load The Y Register
        /// Function:    Y = M
        /// Flags Out:   N, Z
        /// </summary>
        /// <returns></returns>
        private byte LDY()
        {
            // Which cycle did the addressing mode operations end?
            byte opcodeStartCycle = instr_state_bytes[STATE_ADDR_MODE_COMPLETED_CYCLE];

            // This is a read instruction, so everything should be done on the last cycle.
            if (cycles == opcodeStartCycle)
            {
                y = fetched;
                testAndSet(FLAGS6502.Z, y);
                testAndSet(FLAGS6502.N, y);
            }
            return 1;
        }

        #endregion // Load instructions

        /// <summary>
        /// No operation
        /// </summary>
        /// <returns></returns>
        /// <remarks>
        /// Sadly not all NOPs are equal, Ive added a few here
        /// based on https://wiki.nesdev.com/w/index.php/CPU_unofficial_opcodes
        /// and will add more based on game compatibility, and ultimately
        /// I'd like to cover all illegal opcodes too
        /// </remarks>
        private byte NOP()
        {
            //Log.Debug($"[{clock_count}] NOP Opcode: 0x{opcode:X2}");
            switch (opcode)
            {
                case 0x1C:
                case 0x3C:
                case 0x5C:
                case 0x7C:
                case 0xDC:
                case 0xFC:
                    return 1;
            }
            return 0;
        }

        #region Stack Instructions
        /// <summary>
        /// Instruction: Push Accumulator to Stack
        /// Function:    A -> stack
        /// </summary>
        /// <returns></returns>
        private byte PHA()
        {
            // Dummy read
            if (cycles == 1)
            {
                read(pc);
            }
            else if (cycles == 2)
            {
                push(a);
            }
            return 0;
        }

        /// <summary>
        /// Instruction: Push Status Register to Stack
        /// Function:    FLAGS -> stack
        /// Flags Out:   B, U
        /// </summary>
        /// <returns></returns>
        private byte PHP()
        {
            // Dummy read
            if (cycles == 1)
            {
                read(pc);
            }
            else if (cycles == 2)
            {
                write((ushort)(ADDR_STACK + sp), (byte)(status | FLAGS6502.B | FLAGS6502.U));
                setFlag(FLAGS6502.B, false);
                setFlag(FLAGS6502.U, false);
                sp--;
            }
            return 0;
        }

        /// <summary>
        /// Instruction: Pop Accumulator off Stack
        /// Function:    A <- stack
        /// Flags Out:   N, Z
        /// </summary>
        /// <returns></returns>
        private byte PLA()
        {
            // Dummy read
            if (cycles == 1)
            {
                read(pc);
            }
            else if (cycles == 3)
            {
                a = pop();
                testAndSet(FLAGS6502.Z, a);
                testAndSet(FLAGS6502.N, a);
            };
            return 0;
        }

        /// <summary>
        /// Instruction: Pop Status Register off Stack
        /// Function:    FLAGS <- stack
        /// Flags Out:   U
        /// </summary>
        /// <returns>
        /// </returns>
        private byte PLP()
        {
            // Dummy read
            if (cycles == 1)
            {
                read(pc);
            }
            else if (cycles == 3)
            {
                bool prevI = getFlag(FLAGS6502.I) == 1;
                status = (FLAGS6502)pop();
                if (!prevI && getFlag(FLAGS6502.I) == 1)
                    _irqDisablePending = true;
                if (prevI && getFlag(FLAGS6502.I) == 0)
                    _irqEnableLatency = 1;
                setFlag(FLAGS6502.U, true);
            };
            return 0;
        }
        #endregion // Stack Instructions

        /// <summary>
        /// Instruction: Return From Interrupt
        /// Function:    FLAGS <- stack, PC <- stack
        /// Flags Out:   B, U
        /// </summary>
        /// <returns></returns>
        private byte RTI()
        {
            // Dummy read
            if (cycles == 1)
            {
                read(pc);
            }
            else if (cycles == 3)
            {
                status = (FLAGS6502)pop();
                status &= ~FLAGS6502.B;
                status &= ~FLAGS6502.U;
                return 0;
            }
            else if (cycles == 4)
            {
                pc = pop();
            }
            else if (cycles == 5)
            {
                pc |= (ushort)(pop() << 8);
            };
            return 0;
        }

        /// <summary>
        /// Instruction: Return From Subroutine
        /// Function:    PC <- stack, PC = PC + 1
        /// Flags Out:   
        /// </summary>
        /// <returns></returns>
        private byte RTS()
        {
            // Dummy read
            if (cycles == 1)
            {
                read(pc);
            }
            else if (cycles == 3)
            {
                pc = pop();
            }
            else if (cycles == 4)
            {
                pc |= (ushort)(pop() << 8);
            }
            else if (cycles == 5)
            {
                pc++;
            }

            return 0;
        }

        #region Store instructions

        /// <summary>
        /// Instruction: Store Accumulator at Address
        /// Function:    M = A
        /// Flags Out:
        /// </summary>
        /// <returns></returns>
        private byte STA()
        {
            // Which cycle did the addressing mode operations end?
            byte opcodeStartCycle = instr_state_bytes[STATE_ADDR_MODE_COMPLETED_CYCLE];

            // This is a write instruction, so everything should be done on the last cycle.
            if (cycles == opcodeStartCycle)
            {
                write(addr_abs, a);
            }
            return 0;
        }

        /// <summary>
        /// Instruction: Store X Register at Address
        /// Function:    M = X
        /// Flags Out:
        /// </summary>
        /// <returns></returns>
        private byte STX()
        {
            // Which cycle did the addressing mode operations end?
            byte opcodeStartCycle = instr_state_bytes[STATE_ADDR_MODE_COMPLETED_CYCLE];

            // This is a write instruction, so everything should be done on the last cycle.
            if (cycles == opcodeStartCycle)
            {
                write(addr_abs, x);
            }
            return 0;
        }

        /// <summary>
        /// Instruction: Store Y Register at Address
        /// Function:    M = Y
        /// Flags Out:
        /// </summary>
        /// <returns></returns>
        private byte STY()
        {
            // Which cycle did the addressing mode operations end?
            byte opcodeStartCycle = instr_state_bytes[STATE_ADDR_MODE_COMPLETED_CYCLE];

            // This is a write instruction, so everything should be done on the last cycle.
            if (cycles == opcodeStartCycle)
            {
                write(addr_abs, y);
            }
            return 0;
        }

        #endregion // Store instructions

        #region Transfer instructions

        /// <summary>
        /// Instruction: Transfer Accumulator to X Register
        /// Function:    X = A
        /// Flags Out:   N, Z
        /// </summary>
        /// <returns></returns>
        private byte TAX()
        {
            if (cycles == 1)
            {
                x = a;
                testAndSet(FLAGS6502.Z, x);
                testAndSet(FLAGS6502.N, x);
            }
            else if (cycles > 1)
            {
                Log.Error($"[{clock_count}] TAX error - incorrect cycle # [cycles={cycles}]");
            }
            return 0;
        }

        /// <summary>
        /// Instruction: Transfer Accumulator to Y Register
        /// Function:    Y = A
        /// Flags Out:   N, Z
        /// </summary>
        /// <returns></returns>
        private byte TAY()
        {
            if (cycles == 1)
            {
                y = a;
                testAndSet(FLAGS6502.Z, y);
                testAndSet(FLAGS6502.N, y);
            }
            else if (cycles > 1)
            {
                Log.Error($"[{clock_count}] TAY error - incorrect cycle # [cycles={cycles}]");
            }
            return 0;
        }

        /// <summary>
        /// Instruction: Transfer Stack Pointer to X Register
        /// Function:    X = sp
        /// Flags Out:   N, Z
        /// </summary>
        /// <returns></returns>
        private byte TSX()
        {
            if (cycles == 1)
            {
                x = sp;
                testAndSet(FLAGS6502.Z, x);
                testAndSet(FLAGS6502.N, x);
            }
            else if (cycles > 1)
            {
                Log.Error($"[{clock_count}] TSX error - incorrect cycle # [cycles={cycles}]");
            }
            return 0;
        }

        /// <summary>
        /// Instruction: Transfer X Register to Accumulator
        /// Function:    A = X
        /// Flags Out:   N, Z
        /// </summary>
        /// <returns></returns>
        private byte TXA()
        {
            if (cycles == 1)
            {
                a = x;
                testAndSet(FLAGS6502.Z, a);
                testAndSet(FLAGS6502.N, a);
            }
            else if (cycles > 1)
            {
                Log.Error($"[{clock_count}] TXA error - incorrect cycle # [cycles={cycles}]");
            }
            return 0;
        }

        /// <summary>
        /// Instruction: Transfer X Register to Stack Pointer
        /// Function:    SP = X
        /// Flags Out:
        /// </summary>
        /// <returns></returns>
        private byte TXS()
        {
            if (cycles == 1)
            {
                sp = x;
            }
            else if (cycles > 1)
            {
                Log.Error($"[{clock_count}] TXS error - incorrect cycle # [cycles={cycles}]");
            }
            return 0;
        }

        /// <summary>
        /// Instruction: Transfer Y Register to Accumulator
        /// Function:    A = Y
        /// Flags Out:   N, Z
        /// </summary>
        /// <returns></returns>
        private byte TYA()
        {
            if (cycles == 1)
            {
                a = y;
                testAndSet(FLAGS6502.Z, a);
                testAndSet(FLAGS6502.N, a);
            }
            else if (cycles > 1)
            {
                Log.Error($"[{clock_count}] TYA error - incorrect cycle # [cycles={cycles}]");
            }
            return 0;
        }

        #endregion // Transfer instructions

        /// <summary>
        /// All "unofficial" opcodes will be routed here.
        /// </summary>
        /// <returns></returns>
        private byte XXX()
        {
            return 0;
        }
#endregion // OpCodes

        private void build_lookup()
        {
            this.opcode_lookup = new List<Instruction>() {
                new Instruction() { name = "BRK", operation = BRK, addr_mode = IMM, instr_type = CPUInstructionType.Special, cycles = 7 },
                new Instruction() { name = "ORA", operation = ORA, addr_mode = IZX, instr_type = CPUInstructionType.Read,    cycles = 6 },
                new Instruction() { name = "???", operation = XXX, addr_mode = IMP, instr_type = CPUInstructionType.Special, cycles = 2 },
                new Instruction() { name = "???", operation = XXX, addr_mode = IMP, instr_type = CPUInstructionType.Special, cycles = 8 },
                new Instruction() { name = "???", operation = NOP, addr_mode = ZP0, instr_type = CPUInstructionType.Read,    cycles = 3 },
                new Instruction() { name = "ORA", operation = ORA, addr_mode = ZP0, instr_type = CPUInstructionType.Read,    cycles = 3 },
                new Instruction() { name = "ASL", operation = ASL, addr_mode = ZP0, instr_type = CPUInstructionType.R_M_W,   cycles = 5 },
                new Instruction() { name = "???", operation = XXX, addr_mode = IMP, instr_type = CPUInstructionType.Special, cycles = 5 },
                new Instruction() { name = "PHP", operation = PHP, addr_mode = IMP, instr_type = CPUInstructionType.R_W,     cycles = 3 },
                new Instruction() { name = "ORA", operation = ORA, addr_mode = IMM, instr_type = CPUInstructionType.Read,    cycles = 2 },
                new Instruction() { name = "ASL", operation = ASL, addr_mode = IMP, instr_type = CPUInstructionType.R_M_W,   cycles = 2 },
                new Instruction() { name = "???", operation = XXX, addr_mode = IMP, instr_type = CPUInstructionType.Special, cycles = 2 },
                new Instruction() { name = "???", operation = NOP, addr_mode = ABS, instr_type = CPUInstructionType.Read,    cycles = 4 },
                new Instruction() { name = "ORA", operation = ORA, addr_mode = ABS, instr_type = CPUInstructionType.Read,    cycles = 4 },
                new Instruction() { name = "ASL", operation = ASL, addr_mode = ABS, instr_type = CPUInstructionType.R_M_W,   cycles = 6 },
                new Instruction() { name = "???", operation = XXX, addr_mode = IMP, instr_type = CPUInstructionType.Special, cycles = 6 },
                new Instruction() { name = "BPL", operation = BPL, addr_mode = REL, instr_type = CPUInstructionType.Branch,  cycles = 2 }, // 0x10
                new Instruction() { name = "ORA", operation = ORA, addr_mode = IZY, instr_type = CPUInstructionType.Read,    cycles = 5 },
                new Instruction() { name = "???", operation = XXX, addr_mode = IMP, instr_type = CPUInstructionType.Special, cycles = 2 },
                new Instruction() { name = "???", operation = XXX, addr_mode = IMP, instr_type = CPUInstructionType.Special, cycles = 8 },
                new Instruction() { name = "???", operation = NOP, addr_mode = ZPX, instr_type = CPUInstructionType.Read,    cycles = 4 },
                new Instruction() { name = "ORA", operation = ORA, addr_mode = ZPX, instr_type = CPUInstructionType.Read,    cycles = 4 },
                new Instruction() { name = "ASL", operation = ASL, addr_mode = ZPX, instr_type = CPUInstructionType.R_M_W,   cycles = 6 },
                new Instruction() { name = "???", operation = XXX, addr_mode = IMP, instr_type = CPUInstructionType.Special, cycles = 6 },
                new Instruction() { name = "CLC", operation = CLC, addr_mode = IMP, instr_type = CPUInstructionType.Read,    cycles = 2 },
                new Instruction() { name = "ORA", operation = ORA, addr_mode = ABY, instr_type = CPUInstructionType.Read,    cycles = 4 },
                new Instruction() { name = "???", operation = NOP, addr_mode = IMP, instr_type = CPUInstructionType.Read,    cycles = 2 },
                new Instruction() { name = "???", operation = XXX, addr_mode = IMP, instr_type = CPUInstructionType.Special, cycles = 7 },
                new Instruction() { name = "???", operation = NOP, addr_mode = ABX, instr_type = CPUInstructionType.Read,    cycles = 4 }, // 0x1C
                new Instruction() { name = "ORA", operation = ORA, addr_mode = ABX, instr_type = CPUInstructionType.Read,    cycles = 4 },
                new Instruction() { name = "ASL", operation = ASL, addr_mode = ABX, instr_type = CPUInstructionType.R_M_W,   cycles = 7 },
                new Instruction() { name = "???", operation = XXX, addr_mode = IMP, instr_type = CPUInstructionType.Special, cycles = 7 },
                new Instruction() { name = "JSR", operation = JSR, addr_mode = ABS, instr_type = CPUInstructionType.Branch,  cycles = 6 }, // 0x20
                new Instruction() { name = "AND", operation = AND, addr_mode = IZX, instr_type = CPUInstructionType.Read,    cycles = 6 },
                new Instruction() { name = "???", operation = XXX, addr_mode = IMP, instr_type = CPUInstructionType.Special, cycles = 2 },
                new Instruction() { name = "???", operation = XXX, addr_mode = IMP, instr_type = CPUInstructionType.Special, cycles = 8 },
                new Instruction() { name = "BIT", operation = BIT, addr_mode = ZP0, instr_type = CPUInstructionType.Read,    cycles = 3 },
                new Instruction() { name = "AND", operation = AND, addr_mode = ZP0, instr_type = CPUInstructionType.Read,    cycles = 3 },
                new Instruction() { name = "ROL", operation = ROL, addr_mode = ZP0, instr_type = CPUInstructionType.R_M_W,   cycles = 5 },
                new Instruction() { name = "???", operation = XXX, addr_mode = IMP, instr_type = CPUInstructionType.Special, cycles = 5 },
                new Instruction() { name = "PLP", operation = PLP, addr_mode = IMP, instr_type = CPUInstructionType.Special, cycles = 4 },
                new Instruction() { name = "AND", operation = AND, addr_mode = IMM, instr_type = CPUInstructionType.Read,    cycles = 2 },
                new Instruction() { name = "ROL", operation = ROL, addr_mode = IMP, instr_type = CPUInstructionType.R_M_W,   cycles = 2 },
                new Instruction() { name = "???", operation = XXX, addr_mode = IMP, instr_type = CPUInstructionType.Special, cycles = 2 },
                new Instruction() { name = "BIT", operation = BIT, addr_mode = ABS, instr_type = CPUInstructionType.Read,    cycles = 4 },
                new Instruction() { name = "AND", operation = AND, addr_mode = ABS, instr_type = CPUInstructionType.Read,    cycles = 4 },
                new Instruction() { name = "ROL", operation = ROL, addr_mode = ABS, instr_type = CPUInstructionType.R_M_W,   cycles = 6 },
                new Instruction() { name = "???", operation = XXX, addr_mode = IMP, instr_type = CPUInstructionType.Special, cycles = 6 },
                new Instruction() { name = "BMI", operation = BMI, addr_mode = REL, instr_type = CPUInstructionType.Branch,  cycles = 2 }, // 0x30
                new Instruction() { name = "AND", operation = AND, addr_mode = IZY, instr_type = CPUInstructionType.Read,    cycles = 5 },
                new Instruction() { name = "???", operation = XXX, addr_mode = IMP, instr_type = CPUInstructionType.Special, cycles = 2 },
                new Instruction() { name = "???", operation = XXX, addr_mode = IMP, instr_type = CPUInstructionType.Special, cycles = 8 },
                new Instruction() { name = "???", operation = NOP, addr_mode = ZPX, instr_type = CPUInstructionType.Read,    cycles = 4 }, // 0x34
                new Instruction() { name = "AND", operation = AND, addr_mode = ZPX, instr_type = CPUInstructionType.Read,    cycles = 4 },
                new Instruction() { name = "ROL", operation = ROL, addr_mode = ZPX, instr_type = CPUInstructionType.R_M_W,   cycles = 6 },
                new Instruction() { name = "???", operation = XXX, addr_mode = IMP, instr_type = CPUInstructionType.Special, cycles = 6 },
                new Instruction() { name = "SEC", operation = SEC, addr_mode = IMP, instr_type = CPUInstructionType.Special, cycles = 2 },
                new Instruction() { name = "AND", operation = AND, addr_mode = ABY, instr_type = CPUInstructionType.Read,    cycles = 4 },
                new Instruction() { name = "???", operation = NOP, addr_mode = IMP, instr_type = CPUInstructionType.Read,    cycles = 2 },
                new Instruction() { name = "???", operation = XXX, addr_mode = IMP, instr_type = CPUInstructionType.Special, cycles = 7 },
                new Instruction() { name = "???", operation = NOP, addr_mode = ABX, instr_type = CPUInstructionType.Read,    cycles = 4 }, // 0x3C
                new Instruction() { name = "AND", operation = AND, addr_mode = ABX, instr_type = CPUInstructionType.Read,    cycles = 4 },
                new Instruction() { name = "ROL", operation = ROL, addr_mode = ABX, instr_type = CPUInstructionType.R_M_W,   cycles = 7 },
                new Instruction() { name = "???", operation = XXX, addr_mode = IMP, instr_type = CPUInstructionType.Special, cycles = 7 },
                new Instruction() { name = "RTI", operation = RTI, addr_mode = IMP, instr_type = CPUInstructionType.Special, cycles = 6 }, // 0x40
                new Instruction() { name = "EOR", operation = EOR, addr_mode = IZX, instr_type = CPUInstructionType.Read,    cycles = 6 },
                new Instruction() { name = "???", operation = XXX, addr_mode = IMP, instr_type = CPUInstructionType.Special, cycles = 2 },
                new Instruction() { name = "???", operation = XXX, addr_mode = IMP, instr_type = CPUInstructionType.Special, cycles = 8 },
                new Instruction() { name = "???", operation = NOP, addr_mode = ZP0, instr_type = CPUInstructionType.Read,    cycles = 3 }, // 0x44
                new Instruction() { name = "EOR", operation = EOR, addr_mode = ZP0, instr_type = CPUInstructionType.Read,    cycles = 3 },
                new Instruction() { name = "LSR", operation = LSR, addr_mode = ZP0, instr_type = CPUInstructionType.R_M_W,   cycles = 5 },
                new Instruction() { name = "???", operation = XXX, addr_mode = IMP, instr_type = CPUInstructionType.Special, cycles = 5 },
                new Instruction() { name = "PHA", operation = PHA, addr_mode = IMP, instr_type = CPUInstructionType.Special, cycles = 3 },
                new Instruction() { name = "EOR", operation = EOR, addr_mode = IMM, instr_type = CPUInstructionType.Read,    cycles = 2 },
                new Instruction() { name = "LSR", operation = LSR, addr_mode = IMP, instr_type = CPUInstructionType.R_M_W,   cycles = 2 },
                new Instruction() { name = "???", operation = XXX, addr_mode = IMP, instr_type = CPUInstructionType.Special, cycles = 2 },
                new Instruction() { name = "JMP", operation = JMP, addr_mode = ABS, instr_type = CPUInstructionType.Branch,  cycles = 3 },
                new Instruction() { name = "EOR", operation = EOR, addr_mode = ABS, instr_type = CPUInstructionType.Read,    cycles = 4 },
                new Instruction() { name = "LSR", operation = LSR, addr_mode = ABS, instr_type = CPUInstructionType.R_M_W,   cycles = 6 },
                new Instruction() { name = "???", operation = XXX, addr_mode = IMP, instr_type = CPUInstructionType.Special, cycles = 6 },
                new Instruction() { name = "BVC", operation = BVC, addr_mode = REL, instr_type = CPUInstructionType.Branch,  cycles = 2 }, // 0x50
                new Instruction() { name = "EOR", operation = EOR, addr_mode = IZY, instr_type = CPUInstructionType.Read,    cycles = 5 },
                new Instruction() { name = "???", operation = XXX, addr_mode = IMP, instr_type = CPUInstructionType.Special, cycles = 2 },
                new Instruction() { name = "???", operation = XXX, addr_mode = IMP, instr_type = CPUInstructionType.Special, cycles = 8 },
                new Instruction() { name = "???", operation = NOP, addr_mode = ZPX, instr_type = CPUInstructionType.Read,    cycles = 4 }, // 0x54
                new Instruction() { name = "EOR", operation = EOR, addr_mode = ZPX, instr_type = CPUInstructionType.Read,    cycles = 4 },
                new Instruction() { name = "LSR", operation = LSR, addr_mode = ZPX, instr_type = CPUInstructionType.R_M_W,   cycles = 6 },
                new Instruction() { name = "???", operation = XXX, addr_mode = IMP, instr_type = CPUInstructionType.Special, cycles = 6 },
                new Instruction() { name = "CLI", operation = CLI, addr_mode = IMP, instr_type = CPUInstructionType.Special, cycles = 2 },
                new Instruction() { name = "EOR", operation = EOR, addr_mode = ABY, instr_type = CPUInstructionType.Read,    cycles = 4 },
                new Instruction() { name = "???", operation = NOP, addr_mode = IMP, instr_type = CPUInstructionType.Read,    cycles = 2 },
                new Instruction() { name = "???", operation = XXX, addr_mode = IMP, instr_type = CPUInstructionType.Special, cycles = 7 },
                new Instruction() { name = "???", operation = NOP, addr_mode = ABX, instr_type = CPUInstructionType.Read,    cycles = 4 }, // 0x5C
                new Instruction() { name = "EOR", operation = EOR, addr_mode = ABX, instr_type = CPUInstructionType.Read,    cycles = 4 },
                new Instruction() { name = "LSR", operation = LSR, addr_mode = ABX, instr_type = CPUInstructionType.R_M_W,   cycles = 7 },
                new Instruction() { name = "???", operation = XXX, addr_mode = IMP, instr_type = CPUInstructionType.Special, cycles = 7 },
                new Instruction() { name = "RTS", operation = RTS, addr_mode = IMP, instr_type = CPUInstructionType.Special, cycles = 6 }, // 0x60
                new Instruction() { name = "ADC", operation = ADC, addr_mode = IZX, instr_type = CPUInstructionType.Read,    cycles = 6 },
                new Instruction() { name = "???", operation = XXX, addr_mode = IMP, instr_type = CPUInstructionType.Special, cycles = 2 },
                new Instruction() { name = "???", operation = XXX, addr_mode = IMP, instr_type = CPUInstructionType.Special, cycles = 8 },
                new Instruction() { name = "???", operation = NOP, addr_mode = ZP0, instr_type = CPUInstructionType.Read,    cycles = 3 }, // 0x64
                new Instruction() { name = "ADC", operation = ADC, addr_mode = ZP0, instr_type = CPUInstructionType.Read,    cycles = 3 },
                new Instruction() { name = "ROR", operation = ROR, addr_mode = ZP0, instr_type = CPUInstructionType.R_M_W,   cycles = 5 },
                new Instruction() { name = "???", operation = XXX, addr_mode = IMP, instr_type = CPUInstructionType.Special, cycles = 5 },
                new Instruction() { name = "PLA", operation = PLA, addr_mode = IMP, instr_type = CPUInstructionType.Special, cycles = 4 },
                new Instruction() { name = "ADC", operation = ADC, addr_mode = IMM, instr_type = CPUInstructionType.Read,    cycles = 2 },
                new Instruction() { name = "ROR", operation = ROR, addr_mode = IMP, instr_type = CPUInstructionType.R_M_W,   cycles = 2 },
                new Instruction() { name = "???", operation = XXX, addr_mode = IMP, instr_type = CPUInstructionType.Special, cycles = 2 },
                new Instruction() { name = "JMP", operation = JMP, addr_mode = IND, instr_type = CPUInstructionType.Branch,  cycles = 5 },
                new Instruction() { name = "ADC", operation = ADC, addr_mode = ABS, instr_type = CPUInstructionType.Read,    cycles = 4 },
                new Instruction() { name = "ROR", operation = ROR, addr_mode = ABS, instr_type = CPUInstructionType.R_M_W,   cycles = 6 },
                new Instruction() { name = "???", operation = XXX, addr_mode = IMP, instr_type = CPUInstructionType.Special, cycles = 6 },
                new Instruction() { name = "BVS", operation = BVS, addr_mode = REL, instr_type = CPUInstructionType.Branch,  cycles = 2 }, // 0x70
                new Instruction() { name = "ADC", operation = ADC, addr_mode = IZY, instr_type = CPUInstructionType.Read,    cycles = 5 },
                new Instruction() { name = "???", operation = XXX, addr_mode = IMP, instr_type = CPUInstructionType.Special, cycles = 2 },
                new Instruction() { name = "???", operation = XXX, addr_mode = IMP, instr_type = CPUInstructionType.Special, cycles = 8 },
                new Instruction() { name = "???", operation = NOP, addr_mode = ZPX, instr_type = CPUInstructionType.Read,    cycles = 4 }, // 0x74
                new Instruction() { name = "ADC", operation = ADC, addr_mode = ZPX, instr_type = CPUInstructionType.Read,    cycles = 4 },
                new Instruction() { name = "ROR", operation = ROR, addr_mode = ZPX, instr_type = CPUInstructionType.R_M_W,   cycles = 6 },
                new Instruction() { name = "???", operation = XXX, addr_mode = IMP, instr_type = CPUInstructionType.Special, cycles = 6 },
                new Instruction() { name = "SEI", operation = SEI, addr_mode = IMP, instr_type = CPUInstructionType.Special, cycles = 2 },
                new Instruction() { name = "ADC", operation = ADC, addr_mode = ABY, instr_type = CPUInstructionType.Read,    cycles = 4 },
                new Instruction() { name = "???", operation = NOP, addr_mode = IMP, instr_type = CPUInstructionType.Read,    cycles = 2 },
                new Instruction() { name = "???", operation = XXX, addr_mode = IMP, instr_type = CPUInstructionType.Special, cycles = 7 },
                new Instruction() { name = "???", operation = NOP, addr_mode = ABX, instr_type = CPUInstructionType.Read,    cycles = 4 }, // 0x7C
                new Instruction() { name = "ADC", operation = ADC, addr_mode = ABX, instr_type = CPUInstructionType.Read,    cycles = 4 },
                new Instruction() { name = "ROR", operation = ROR, addr_mode = ABX, instr_type = CPUInstructionType.R_M_W,   cycles = 7 },
                new Instruction() { name = "???", operation = XXX, addr_mode = IMP, instr_type = CPUInstructionType.Special, cycles = 7 },
                new Instruction() { name = "???", operation = NOP, addr_mode = IMM, instr_type = CPUInstructionType.Read,    cycles = 2 }, // 0x80
                new Instruction() { name = "STA", operation = STA, addr_mode = IZX, instr_type = CPUInstructionType.Write,   cycles = 6 },
                new Instruction() { name = "???", operation = NOP, addr_mode = IMM, instr_type = CPUInstructionType.Read,    cycles = 2 },
                new Instruction() { name = "???", operation = XXX, addr_mode = IMP, instr_type = CPUInstructionType.Special, cycles = 6 },
                new Instruction() { name = "STY", operation = STY, addr_mode = ZP0, instr_type = CPUInstructionType.Write,   cycles = 3 },
                new Instruction() { name = "STA", operation = STA, addr_mode = ZP0, instr_type = CPUInstructionType.Write,   cycles = 3 },
                new Instruction() { name = "STX", operation = STX, addr_mode = ZP0, instr_type = CPUInstructionType.Write,   cycles = 3 },
                new Instruction() { name = "???", operation = XXX, addr_mode = IMP, instr_type = CPUInstructionType.Special, cycles = 3 },
                new Instruction() { name = "DEY", operation = DEY, addr_mode = IMP, instr_type = CPUInstructionType.Special, cycles = 2 },
                new Instruction() { name = "???", operation = NOP, addr_mode = IMM, instr_type = CPUInstructionType.Read,    cycles = 2 }, // 0x89
                new Instruction() { name = "TXA", operation = TXA, addr_mode = IMP, instr_type = CPUInstructionType.Special, cycles = 2 },
                new Instruction() { name = "???", operation = XXX, addr_mode = IMP, instr_type = CPUInstructionType.Special, cycles = 2 },
                new Instruction() { name = "STY", operation = STY, addr_mode = ABS, instr_type = CPUInstructionType.Write,   cycles = 4 },
                new Instruction() { name = "STA", operation = STA, addr_mode = ABS, instr_type = CPUInstructionType.Write,   cycles = 4 },
                new Instruction() { name = "STX", operation = STX, addr_mode = ABS, instr_type = CPUInstructionType.Write,   cycles = 4 },
                new Instruction() { name = "???", operation = XXX, addr_mode = IMP, instr_type = CPUInstructionType.Special, cycles = 4 },
                new Instruction() { name = "BCC", operation = BCC, addr_mode = REL, instr_type = CPUInstructionType.Branch,  cycles = 2 }, // 0x90
                new Instruction() { name = "STA", operation = STA, addr_mode = IZY, instr_type = CPUInstructionType.Write,   cycles = 6 },
                new Instruction() { name = "???", operation = XXX, addr_mode = IMP, instr_type = CPUInstructionType.Special, cycles = 2 },
                new Instruction() { name = "???", operation = XXX, addr_mode = IMP, instr_type = CPUInstructionType.Special, cycles = 6 },
                new Instruction() { name = "STY", operation = STY, addr_mode = ZPX, instr_type = CPUInstructionType.Write,   cycles = 4 },
                new Instruction() { name = "STA", operation = STA, addr_mode = ZPX, instr_type = CPUInstructionType.Write,   cycles = 4 },
                new Instruction() { name = "STX", operation = STX, addr_mode = ZPY, instr_type = CPUInstructionType.Write,   cycles = 4 },
                new Instruction() { name = "???", operation = XXX, addr_mode = IMP, instr_type = CPUInstructionType.Special, cycles = 4 },
                new Instruction() { name = "TYA", operation = TYA, addr_mode = IMP, instr_type = CPUInstructionType.Special, cycles = 2 },
                new Instruction() { name = "STA", operation = STA, addr_mode = ABY, instr_type = CPUInstructionType.Write,   cycles = 5 },
                new Instruction() { name = "TXS", operation = TXS, addr_mode = IMP, instr_type = CPUInstructionType.Special, cycles = 2 },
                new Instruction() { name = "???", operation = XXX, addr_mode = IMP, instr_type = CPUInstructionType.Special, cycles = 5 },
                new Instruction() { name = "???", operation = NOP, addr_mode = IMP, instr_type = CPUInstructionType.Read,    cycles = 5 },
                new Instruction() { name = "STA", operation = STA, addr_mode = ABX, instr_type = CPUInstructionType.Write,   cycles = 5 },
                new Instruction() { name = "???", operation = XXX, addr_mode = IMP, instr_type = CPUInstructionType.Special, cycles = 5 },
                new Instruction() { name = "???", operation = XXX, addr_mode = IMP, instr_type = CPUInstructionType.Special, cycles = 5 },
                new Instruction() { name = "LDY", operation = LDY, addr_mode = IMM, instr_type = CPUInstructionType.Read,    cycles = 2 }, // 0xA0
                new Instruction() { name = "LDA", operation = LDA, addr_mode = IZX, instr_type = CPUInstructionType.Read,    cycles = 6 },
                new Instruction() { name = "LDX", operation = LDX, addr_mode = IMM, instr_type = CPUInstructionType.Read,    cycles = 2 },
                new Instruction() { name = "???", operation = XXX, addr_mode = IMP, instr_type = CPUInstructionType.Special, cycles = 6 },
                new Instruction() { name = "LDY", operation = LDY, addr_mode = ZP0, instr_type = CPUInstructionType.Read,    cycles = 3 },
                new Instruction() { name = "LDA", operation = LDA, addr_mode = ZP0, instr_type = CPUInstructionType.Read,    cycles = 3 },
                new Instruction() { name = "LDX", operation = LDX, addr_mode = ZP0, instr_type = CPUInstructionType.Read,    cycles = 3 },
                new Instruction() { name = "???", operation = XXX, addr_mode = IMP, instr_type = CPUInstructionType.Special, cycles = 3 },
                new Instruction() { name = "TAY", operation = TAY, addr_mode = IMP, instr_type = CPUInstructionType.Special, cycles = 2 },
                new Instruction() { name = "LDA", operation = LDA, addr_mode = IMM, instr_type = CPUInstructionType.Read,    cycles = 2 },
                new Instruction() { name = "TAX", operation = TAX, addr_mode = IMP, instr_type = CPUInstructionType.Special, cycles = 2 },
                new Instruction() { name = "???", operation = XXX, addr_mode = IMP, instr_type = CPUInstructionType.Special, cycles = 2 },
                new Instruction() { name = "LDY", operation = LDY, addr_mode = ABS, instr_type = CPUInstructionType.Read,    cycles = 4 },
                new Instruction() { name = "LDA", operation = LDA, addr_mode = ABS, instr_type = CPUInstructionType.Read,    cycles = 4 },
                new Instruction() { name = "LDX", operation = LDX, addr_mode = ABS, instr_type = CPUInstructionType.Read,    cycles = 4 },
                new Instruction() { name = "???", operation = XXX, addr_mode = IMP, instr_type = CPUInstructionType.Special, cycles = 4 },
                new Instruction() { name = "BCS", operation = BCS, addr_mode = REL, instr_type = CPUInstructionType.Branch,  cycles = 2 }, // 0xB0
                new Instruction() { name = "LDA", operation = LDA, addr_mode = IZY, instr_type = CPUInstructionType.Read,    cycles = 5 },
                new Instruction() { name = "???", operation = XXX, addr_mode = IMP, instr_type = CPUInstructionType.Special, cycles = 2 },
                new Instruction() { name = "???", operation = XXX, addr_mode = IMP, instr_type = CPUInstructionType.Special, cycles = 5 },
                new Instruction() { name = "LDY", operation = LDY, addr_mode = ZPX, instr_type = CPUInstructionType.Read,    cycles = 4 },
                new Instruction() { name = "LDA", operation = LDA, addr_mode = ZPX, instr_type = CPUInstructionType.Read,    cycles = 4 },
                new Instruction() { name = "LDX", operation = LDX, addr_mode = ZPY, instr_type = CPUInstructionType.Read,    cycles = 4 },
                new Instruction() { name = "???", operation = XXX, addr_mode = IMP, instr_type = CPUInstructionType.Special, cycles = 4 },
                new Instruction() { name = "CLV", operation = CLV, addr_mode = IMP, instr_type = CPUInstructionType.Special, cycles = 2 },
                new Instruction() { name = "LDA", operation = LDA, addr_mode = ABY, instr_type = CPUInstructionType.Read,    cycles = 4 },
                new Instruction() { name = "TSX", operation = TSX, addr_mode = IMP, instr_type = CPUInstructionType.Special, cycles = 2 },
                new Instruction() { name = "???", operation = XXX, addr_mode = IMP, instr_type = CPUInstructionType.Special, cycles = 4 },
                new Instruction() { name = "LDY", operation = LDY, addr_mode = ABX, instr_type = CPUInstructionType.Read,    cycles = 4 },
                new Instruction() { name = "LDA", operation = LDA, addr_mode = ABX, instr_type = CPUInstructionType.Read,    cycles = 4 },
                new Instruction() { name = "LDX", operation = LDX, addr_mode = ABY, instr_type = CPUInstructionType.Read,    cycles = 4 },
                new Instruction() { name = "???", operation = XXX, addr_mode = IMP, instr_type = CPUInstructionType.Special, cycles = 4 },
                new Instruction() { name = "CPY", operation = CPY, addr_mode = IMM, instr_type = CPUInstructionType.Read,    cycles = 2 }, // 0xC0
                new Instruction() { name = "CMP", operation = CMP, addr_mode = IZX, instr_type = CPUInstructionType.Read,    cycles = 6 },
                new Instruction() { name = "???", operation = NOP, addr_mode = IMM, instr_type = CPUInstructionType.Read,    cycles = 2 }, // 0xC2
                new Instruction() { name = "???", operation = XXX, addr_mode = IMP, instr_type = CPUInstructionType.Special, cycles = 8 },
                new Instruction() { name = "CPY", operation = CPY, addr_mode = ZP0, instr_type = CPUInstructionType.Read,    cycles = 3 },
                new Instruction() { name = "CMP", operation = CMP, addr_mode = ZP0, instr_type = CPUInstructionType.Read,    cycles = 3 },
                new Instruction() { name = "DEC", operation = DEC, addr_mode = ZP0, instr_type = CPUInstructionType.R_M_W,   cycles = 5 },
                new Instruction() { name = "???", operation = XXX, addr_mode = IMP, instr_type = CPUInstructionType.Special, cycles = 5 },
                new Instruction() { name = "INY", operation = INY, addr_mode = IMP, instr_type = CPUInstructionType.Special, cycles = 2 },
                new Instruction() { name = "CMP", operation = CMP, addr_mode = IMM, instr_type = CPUInstructionType.Read,    cycles = 2 },
                new Instruction() { name = "DEX", operation = DEX, addr_mode = IMP, instr_type = CPUInstructionType.Special, cycles = 2 },
                new Instruction() { name = "???", operation = XXX, addr_mode = IMP, instr_type = CPUInstructionType.Special, cycles = 2 },
                new Instruction() { name = "CPY", operation = CPY, addr_mode = ABS, instr_type = CPUInstructionType.Read,    cycles = 4 },
                new Instruction() { name = "CMP", operation = CMP, addr_mode = ABS, instr_type = CPUInstructionType.Read,    cycles = 4 },
                new Instruction() { name = "DEC", operation = DEC, addr_mode = ABS, instr_type = CPUInstructionType.R_M_W,   cycles = 6 },
                new Instruction() { name = "???", operation = XXX, addr_mode = IMP, instr_type = CPUInstructionType.Special, cycles = 6 },
                new Instruction() { name = "BNE", operation = BNE, addr_mode = REL, instr_type = CPUInstructionType.Branch,  cycles = 2 }, // 0xD0
                new Instruction() { name = "CMP", operation = CMP, addr_mode = IZY, instr_type = CPUInstructionType.Read,    cycles = 5 },
                new Instruction() { name = "???", operation = XXX, addr_mode = IMP, instr_type = CPUInstructionType.Special, cycles = 2 },
                new Instruction() { name = "???", operation = XXX, addr_mode = IMP, instr_type = CPUInstructionType.Special, cycles = 8 },
                new Instruction() { name = "???", operation = NOP, addr_mode = ZPX, instr_type = CPUInstructionType.Read,    cycles = 4 }, // 0xD4
                new Instruction() { name = "CMP", operation = CMP, addr_mode = ZPX, instr_type = CPUInstructionType.Read,    cycles = 4 },
                new Instruction() { name = "DEC", operation = DEC, addr_mode = ZPX, instr_type = CPUInstructionType.R_M_W,   cycles = 6 },
                new Instruction() { name = "???", operation = XXX, addr_mode = IMP, instr_type = CPUInstructionType.Special, cycles = 6 },
                new Instruction() { name = "CLD", operation = CLD, addr_mode = IMP, instr_type = CPUInstructionType.Special, cycles = 2 },
                new Instruction() { name = "CMP", operation = CMP, addr_mode = ABY, instr_type = CPUInstructionType.Read,    cycles = 4 },
                new Instruction() { name = "NOP", operation = NOP, addr_mode = IMP, instr_type = CPUInstructionType.Read,    cycles = 2 },
                new Instruction() { name = "???", operation = XXX, addr_mode = IMP, instr_type = CPUInstructionType.Special, cycles = 7 },
                new Instruction() { name = "???", operation = NOP, addr_mode = ABX, instr_type = CPUInstructionType.Read,    cycles = 4 }, // 0xDC
                new Instruction() { name = "CMP", operation = CMP, addr_mode = ABX, instr_type = CPUInstructionType.Read,    cycles = 4 },
                new Instruction() { name = "DEC", operation = DEC, addr_mode = ABX, instr_type = CPUInstructionType.R_M_W,   cycles = 7 },
                new Instruction() { name = "???", operation = XXX, addr_mode = IMP, instr_type = CPUInstructionType.Special, cycles = 7 },
                new Instruction() { name = "CPX", operation = CPX, addr_mode = IMM, instr_type = CPUInstructionType.Read,    cycles = 2 }, // 0xE0
                new Instruction() { name = "SBC", operation = SBC, addr_mode = IZX, instr_type = CPUInstructionType.Read,    cycles = 6 },
                new Instruction() { name = "???", operation = NOP, addr_mode = IMM, instr_type = CPUInstructionType.Read,    cycles = 2 }, // 0xE2
                new Instruction() { name = "???", operation = XXX, addr_mode = IMP, instr_type = CPUInstructionType.Special, cycles = 8 },
                new Instruction() { name = "CPX", operation = CPX, addr_mode = ZP0, instr_type = CPUInstructionType.Read,    cycles = 3 },
                new Instruction() { name = "SBC", operation = SBC, addr_mode = ZP0, instr_type = CPUInstructionType.Read,    cycles = 3 },
                new Instruction() { name = "INC", operation = INC, addr_mode = ZP0, instr_type = CPUInstructionType.R_M_W,   cycles = 5 },
                new Instruction() { name = "???", operation = XXX, addr_mode = IMP, instr_type = CPUInstructionType.Special, cycles = 5 },
                new Instruction() { name = "INX", operation = INX, addr_mode = IMP, instr_type = CPUInstructionType.Special, cycles = 2 },
                new Instruction() { name = "SBC", operation = SBC, addr_mode = IMM, instr_type = CPUInstructionType.Read,    cycles = 2 },
                new Instruction() { name = "NOP", operation = NOP, addr_mode = IMP, instr_type = CPUInstructionType.Read,    cycles = 2 },
                new Instruction() { name = "???", operation = SBC, addr_mode = IMM, instr_type = CPUInstructionType.Read,    cycles = 2 }, // 0xEB
                new Instruction() { name = "CPX", operation = CPX, addr_mode = ABS, instr_type = CPUInstructionType.Read,    cycles = 4 },
                new Instruction() { name = "SBC", operation = SBC, addr_mode = ABS, instr_type = CPUInstructionType.Read,    cycles = 4 },
                new Instruction() { name = "INC", operation = INC, addr_mode = ABS, instr_type = CPUInstructionType.R_M_W,   cycles = 6 },
                new Instruction() { name = "???", operation = XXX, addr_mode = IMP, instr_type = CPUInstructionType.Special, cycles = 6 },
                new Instruction() { name = "BEQ", operation = BEQ, addr_mode = REL, instr_type = CPUInstructionType.Branch,  cycles = 2 }, // 0xF0
                new Instruction() { name = "SBC", operation = SBC, addr_mode = IZY, instr_type = CPUInstructionType.Read,    cycles = 5 },
                new Instruction() { name = "???", operation = XXX, addr_mode = IMP, instr_type = CPUInstructionType.Special, cycles = 2 },
                new Instruction() { name = "???", operation = XXX, addr_mode = IMP, instr_type = CPUInstructionType.Special, cycles = 8 },
                new Instruction() { name = "???", operation = NOP, addr_mode = ZPX, instr_type = CPUInstructionType.Read,    cycles = 4 }, // 0xF4
                new Instruction() { name = "SBC", operation = SBC, addr_mode = ZPX, instr_type = CPUInstructionType.Read,    cycles = 4 },
                new Instruction() { name = "INC", operation = INC, addr_mode = ZPX, instr_type = CPUInstructionType.R_M_W,   cycles = 6 },
                new Instruction() { name = "???", operation = XXX, addr_mode = IMP, instr_type = CPUInstructionType.Special, cycles = 6 },
                new Instruction() { name = "SED", operation = SED, addr_mode = IMP, instr_type = CPUInstructionType.Special, cycles = 2 },
                new Instruction() { name = "SBC", operation = SBC, addr_mode = ABY, instr_type = CPUInstructionType.Read,    cycles = 4 },
                new Instruction() { name = "NOP", operation = NOP, addr_mode = IMP, instr_type = CPUInstructionType.Read,    cycles = 2 },
                new Instruction() { name = "???", operation = XXX, addr_mode = IMP, instr_type = CPUInstructionType.Special, cycles = 7 },
                new Instruction() { name = "???", operation = NOP, addr_mode = ABX, instr_type = CPUInstructionType.Read,    cycles = 4 }, // 0xFC
                new Instruction() { name = "SBC", operation = SBC, addr_mode = ABX, instr_type = CPUInstructionType.Read,    cycles = 4 },
                new Instruction() { name = "INC", operation = INC, addr_mode = ABX, instr_type = CPUInstructionType.R_M_W,   cycles = 7 },
                new Instruction() { name = "???", operation = XXX, addr_mode = IMP, instr_type = CPUInstructionType.Special, cycles = 7 }
            };
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private byte push(byte data)
        {
            write((ushort)(ADDR_STACK + sp), data);
            sp--;
            return sp;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private byte pop()
        {
            sp++;
            byte data = read((ushort)(ADDR_STACK + sp));
            return data;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void testAndSet(FLAGS6502 flag, ushort data)
        {
            switch (flag)
            {
                case FLAGS6502.B:
                case FLAGS6502.C:
                case FLAGS6502.D:
                case FLAGS6502.U:
                case FLAGS6502.V:
                    break;
                case FLAGS6502.N:
                    setFlag(flag, (data & 0x0080) != 0);
                    break;
                case FLAGS6502.Z:
                    setFlag(flag, (data & 0x00FF) == 0);
                    break;
            }
        }

#if LOGMODE
        // private Log log;
#endif
    }
}
