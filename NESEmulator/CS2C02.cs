﻿using System;
using System.Collections.Generic;
using csPixelGameEngine;
using NESEmulator.Util;

namespace NESEmulator
{
    /// <summary>
    /// This represents the NES' Picture Processing Unit (PPU) 2C02
    /// </summary>
    public class CS2C02 : InterruptingBusDevice
    {
        private const ushort ADDR_PPU_MIN   = 0x2000;
        private const ushort ADDR_PPU_MAX   = 0x3FFF;
        private const ushort ADDR_PALETTE   = 0x3F00;
        private const ushort ADDR_NAMETABLE = 0x2000;

        private const ushort MAX_PALETTE    = 0x3F;

        private const ushort SCREEN_WIDTH  = 256;
        private const ushort SCREEN_HEIGHT = 240;

        private const ushort CYCLES     = 341;
        private const ushort SCANLINES  = 262;

        public override BusDeviceType DeviceType { get { return BusDeviceType.PPU; } }

        public override event InterruptingDeviceHandler RaiseInterrupt;

        // PPU has it's own bus
        private Bus _ppuBus;

        private PPUStatus _status;
        private PPUMask _mask;
        private PPUControl _control;
        private PPULoopyRegister _vram_addr;
        private PPULoopyRegister _tram_addr;

        // Pixel offset horizontally
        private byte _fineX;

        // Internal communications
        private byte _addressLatch;
        private byte _ppuDataBuffer;

        private Sprite _screen = new Sprite(SCREEN_WIDTH, SCREEN_HEIGHT);

        // Pixel "dot" position information
        private short  _scanline;
        private ushort _cycle;

        // Background rendering
        private byte _bg_nextTileId;
        private byte _bg_nextTileAttrib;
        private byte _bg_nextTileLSB;
        private byte _bg_nextTileMSB;
        private ushort _bg_shifterPatternLo;
        private ushort _bg_shifterPatternHi;
        private ushort _bg_shifterAttribLo;
        private ushort _bg_shifterAttribHi;

        private byte[][] _tblName = new byte[2][];
        private Sprite[] _nameTable = { new Sprite(SCREEN_WIDTH, SCREEN_HEIGHT), new Sprite(SCREEN_WIDTH, SCREEN_HEIGHT) };

        // TODO: This is connected to the PPU's bus, so I think it would be better
        // to make this a BusDevice and attach to _ppuBus.
        private byte[][] _tblPattern = new byte[2][];
        private Sprite[] _patternTable = { new Sprite(128, 128), new Sprite(128, 128) };

        private byte[] _palette = new byte[32];
        private Pixel[] _palScreen = new Pixel[0x40];

        public ObjectAttributeEntry[] OAM = new ObjectAttributeEntry[64];
        private byte _OAMaddr;

        private ObjectAttributeEntry[] _spriteScanline = new ObjectAttributeEntry[8];
        private byte _spriteCount;
        private byte[] _spriteShifterPatternLo = new byte[8];
        private byte[] _spriteShifterPatternHi = new byte[8];
        private bool _spriteZeroHitPossible = false;
        private bool _spriteZeroBeingRendered = false;

        private Cartridge _cartridge;

        private List<PPUCycleNode>[] _cycleOperations;

        private Random _random;

        public CS2C02()
        {
            _random = new Random();

            _status = new PPUStatus();
            _mask   = new PPUMask();

            // Create PPU bus with its devices...

            _tblName[0]     = new byte[1024];
            _tblName[1]     = new byte[1024];
            _tblPattern[0]  = new byte[4096];
            _tblPattern[1]  = new byte[4096];

            buildPalette();
            buildCycleOperations();
        }

        #region Debugging utilities

        public Sprite GetScreen()
        {
            return _screen;
        }

        public byte[] GetNameTableBytes(int tableNum)
        {
            return _tblName[tableNum];
        }

        public Sprite GetNameTable(int i)
        {
            if (i < 2)
                return _nameTable[i];

            return null;
        }

        /// <summary>
        /// This function draw the CHR ROM for a given pattern table into
        /// an olc::Sprite, using a specified palette. Pattern tables consist
        /// of 16x16 "tiles or characters". It is independent of the running
        /// emulation and using it does not change the systems state, though
        /// it gets all the data it needs from the live system. Consequently,
        /// if the game has not yet established palettes or mapped to relevant
        /// CHR ROM banks, the sprite may look empty. This approach permits a 
        /// "live" extraction of the pattern table exactly how the NES, and 
        /// ultimately the player would see it.
        /// 
        /// A tile consists of 8x8 pixels. On the NES, pixels are 2 bits, which
        /// gives an index into 4 different colours of a specific palette. There
        /// are 8 palettes to choose from. Colour "0" in each palette is effectively
        /// considered transparent, as those locations in memory "mirror" the global
        /// background color being used. This mechanics of this are shown in 
        /// detail in ppuRead() & ppuWrite()
        /// 
        /// Characters on NES
        /// ~~~~~~~~~~~~~~~~~
        /// The NES stores characters using 2-bit pixels. These are not stored sequentially
        /// but in singular bit planes. For example:
        ///
        /// 2-Bit Pixels       LSB Bit Plane     MSB Bit Plane
        /// 0 0 0 0 0 0 0 0	  0 0 0 0 0 0 0 0   0 0 0 0 0 0 0 0
        /// 0 1 1 0 0 1 1 0	  0 1 1 0 0 1 1 0   0 0 0 0 0 0 0 0
        /// 0 1 2 0 0 2 1 0	  0 1 1 0 0 1 1 0   0 0 1 0 0 1 0 0
        /// 0 0 0 0 0 0 0 0 =  0 0 0 0 0 0 0 0 + 0 0 0 0 0 0 0 0
        /// 0 1 1 0 0 1 1 0	  0 1 1 0 0 1 1 0   0 0 0 0 0 0 0 0
        /// 0 0 1 1 1 1 0 0	  0 0 1 1 1 1 0 0   0 0 0 0 0 0 0 0
        /// 0 0 0 2 2 0 0 0	  0 0 0 1 1 0 0 0   0 0 0 1 1 0 0 0
        /// 0 0 0 0 0 0 0 0	  0 0 0 0 0 0 0 0   0 0 0 0 0 0 0 0
        ///
        /// The planes are stored as 8 bytes of LSB, followed by 8 bytes of MSB
        /// </summary>
        /// <param name="i"></param>
        /// <param name="palette"></param>
        /// <returns></returns>
        public Sprite GetPatternTable(int i, byte palette)
        {
            // Loop through all the 16x16 tiles
            for (int tileY = 0; tileY < 16; tileY++)
            {
                for (int tileX = 0; tileX < 16; tileX++)
                {
                    // Convert the 2D tile coordinate into an offset into the pattern table memory.
                    int offset = tileY * 256 + tileX * 16;

                    // Loop through 8x8 character/sprite at tile
                    for (int row = 0; row < 8; row++)
                    {
                        // For each row, we need to read both bit planes of the character
                        // in order to extract the least significant and most significant 
                        // bits of the 2 bit pixel value. in the CHR ROM, each character
                        // is stored as 64 bits of lsb, followed by 64 bits of msb. This
                        // conveniently means that two corresponding rows are always 8
                        // bytes apart in memory.
                        byte tileLSB = ppuRead((ushort)(i * 0x1000 + offset + row + 0x0000));
                        byte tileMSB = ppuRead((ushort)(i * 0x1000 + offset + row + 0x0008));

                        // Now we have a single row of the two bit planes for the character
                        // we need to iterate through the 8-bit words, combining them to give
                        // us the final pixel index
                        for (int col = 0; col < 8; col++)
                        {
                            // We can get the index value by simply combining the bits together
                            // but we're only interested in the lsb of the row words because...
                            byte pixel = (byte)((tileLSB & 0x01) + ((tileMSB & 0x01) << 1));

                            // ...we will shift the row words 1 bit right for each column of
                            // the character.
                            tileLSB >>= 1;
                            tileMSB >>= 1;

                            // Now we know the location and NES pixel value for a specific location
                            // in the pattern table, we can translate that to a screen color, and an
                            // (x,y) location in the sprite
                            _patternTable[i].SetPixel(
                                (uint)(tileX * 8 + (7 - col)),  // Because we are using the lsb of the row word first
                                                                // we are effectively reading the row from right
                                                                // to left, so we need to draw the row "backwards"
                                (uint)(tileY * 8 + row),
                                GetColorFromPaletteRam(palette, pixel)
                            );
                        }
                    }
                }
            }

            // Return the sprite representing the pattern table
            return _patternTable[i];
        }

        public bool FrameComplete { get; set; }

        #endregion // Debugging utilities

        #region Bus Communications

        public override void Reset()
        {
            _fineX                  = 0;
            _addressLatch           = 0;
            _ppuDataBuffer          = 0;
            _scanline               = 0;
            _cycle                  = 0;
            _bg_nextTileId          = 0;
            _bg_nextTileAttrib      = 0;
            _bg_nextTileLSB         = 0;
            _bg_nextTileMSB         = 0;
            _bg_shifterPatternLo    = 0;
            _bg_shifterPatternHi    = 0;
            _bg_shifterAttribLo     = 0;
            _bg_shifterAttribHi     = 0;
            _status.reg             = 0;
            _mask.reg               = 0;
            _control.reg            = 0;
            _vram_addr.reg          = 0;
            _tram_addr.reg          = 0;
            _OAMaddr                = 0;
            _cycleOpItr = _cycleOperations[_scanline].GetEnumerator();
            _cycleOpItr.MoveNext();
        }

        public override bool Read(ushort addr, out byte data)
        {
            bool dataRead = false;
            data = 0;

            if (addr >= ADDR_PPU_MIN && addr <= ADDR_PPU_MAX)
            {
                addr &= 0x0007;
                dataRead = true;

                // These are the live PPU registers that repsond
                // to being read from in various ways. Note that not
                // all the registers are capable of being read from
                // so they just return 0x00
                switch (addr)
                {
                    case 0x0000:    // Control
                        break;
                    case 0x0001:    // Mask
                        break;
                    case 0x0002:    // Status
                        // Reading from the status register has the effect of resetting different parts of the circuit. 
                        // Only the top three bits contain status information, however it is possible that some "noise"
                        // gets picked up on the bottom 5 bits which represent the last PPU bus transaction. Some games
                        // "may" use this noise as valid data (even though they probably shouldn't)
                        data = (byte)((_status.reg & 0xE0) | (_ppuDataBuffer & 0x1F));

                        // Clear the vertical blanking flag
                        _status.VerticalBlank = false;

                        // Reset Loopy's Address latch flag
                        _addressLatch = 0;
                        break;
                    case 0x0003:    // OAM Address
                        break;
                    case 0x0004:    // OAM Data
                        data = OAM[_OAMaddr >> 2][_OAMaddr & 0x03];    // maybe??
                        break;
                    case 0x0005:    // Scroll - Not readable
                        break;
                    case 0x0006:    // PPU Address - not readable
                        break;
                    case 0x0007:    // PPU Data
                        // Reads from the NameTable ram get delayed one cycle, so output buffer which contains the data
                        // from the previous read request
                        data = _ppuDataBuffer;
                        // Then update buffer for next time
                        _ppuDataBuffer = ppuRead(_vram_addr.reg);

                        // However, if the address was in the palette range, the data is not delayed, so it returns
                        // immediately
                        if (_vram_addr.reg >= ADDR_PALETTE)
                            data = _ppuDataBuffer;
                        // All reads from the PPU data automatically increment the nametable address depending upon the
                        // mode set in the control register. If set to vertical mode, the increment is 32 so it skips
                        // one whole nametable row; in horizontal mode it just increments by 1, moving to the next column
                        _vram_addr.reg += (ushort)(_control.IncrementMode ? 32 : 1);
                        break;
                }
            }

            return dataRead;
        }

        public override bool Write(ushort addr, byte data)
        {
            bool dataWritten = false;

            if (addr >= ADDR_PPU_MIN && addr <= ADDR_PPU_MAX)
            {
                addr &= 0x0007;
                dataWritten = true;

                switch (addr)
                {
                    case 0x0000:    // Control
                        _control.reg = data;
                        _tram_addr.NameTableX = _control.NameTableX;
                        _tram_addr.NameTableY = _control.NameTableY;
                        break;
                    case 0x0001:    // Mask
                        _mask.reg = data;
                        break;
                    case 0x0002:    // Status
                        break;
                    case 0x0003:    // OAM Address
                        _OAMaddr = data;
                        break;
                    case 0x0004:    // OAM Data
                        OAM[_OAMaddr >> 2][_OAMaddr & 0x03] = data;
                        break;
                    case 0x0005:    // Scroll
                        if (_addressLatch == 0)
                        {
                            // First write to scroll register contains X offset in pixel space which we split into
                            // coarse and fine x values
                            _fineX = (byte)(data & 0x07);
                            _tram_addr.CoarseX = (byte)(data >> 3);
                            _addressLatch = 1;
                        }
                        else
                        {
                            // First write to scroll register contains Y offset in pixel space which we split into
                            // coarse and fine Y values
                            _tram_addr.FineY = (byte)(data & 0x07);
                            _tram_addr.CoarseY = (byte)(data >> 3);
                            _addressLatch = 0;
                        }
                        break;
                    case 0x0006:    // PPU Address
                        if (_addressLatch == 0)
                        {
                            // PPU address bus can be accessed by CPU via the ADDR and DATA registers. The first
                            // write to this register latches the high byte of the address, the second is the low
                            // byte. Note the writes are stored in the tram register
                            _tram_addr.reg = (ushort)(((data & 0x3F) << 8) | (_tram_addr.reg & 0x00FF));
                            _addressLatch = 1;
                        }
                        else
                        {
                            // ...when a whole address has been written, the internal vram address buffer is updated.
                            // Writing to the PPU is unwise during rendering as the PPU will maintain the vram address
                            // automatically while rendering the scanline position.
                            _tram_addr.reg = (ushort)((_tram_addr.reg & 0xFF00) | data);
                            _vram_addr = _tram_addr;
                            _addressLatch = 0;
                        }
                        break;
                    case 0x0007:    // PPU Data
                        ppuWrite(_vram_addr.reg, data);
                        // All writes from PPU data automatically increment the nametable address depending upon the
                        // mode set in the control register. If set to vertical mode, the increment is 32, so it skips
                        // one whole nametable row; in horizontal mode it just increments by 1, moving to the next column.
                        _vram_addr.reg += (ushort)(_control.IncrementMode ? 32 : 1);
                        break;
                }
            }

            return dataWritten;
        }

        // TODO: I think this should be a read from _ppuBus... will investigate later
        private byte ppuRead(ushort addr, bool rdonly = false)
        {
            byte data = 0;
            addr &= ADDR_PPU_MAX;

            // TODO: Eventually loop through ppuBus like we do with the NES bus, attempting to do reads until
            // one "hits"

            if (_cartridge.ppuRead(addr, out data))
            {

            }
            else if (addr >= 0x0000 && addr <= 0x1FFF)  // Pattern (sprite) memory range
            {
                // If the cartridge can't map the address, have a physical location ready here
                data = _tblPattern[(addr & 0x1000) >> 12][addr & 0x0FFF];
            }
            else if (addr >= 0x2000 && addr <= 0x3EFF)  // Nametable memory (VRAM) range
            {
                addr &= 0x0FFF;

                if (_cartridge.mirror == Cartridge.Mirror.VERTICAL)
                {
                    if (addr >= 0x0000 && addr <= 0x03FF)
                        data = _tblName[0][addr & 0x03FF];
                    else if (addr >= 0x0400 && addr <= 0x07FF)
                        data = _tblName[1][addr & 0x03FF];
                    else if (addr >= 0x0800 && addr <= 0x0BFF)
                        data = _tblName[0][addr & 0x03FF];
                    else if (addr >= 0x0C00 && addr <= 0x0FFF)
                        data = _tblName[1][addr & 0x03FF];
                }
                else if (_cartridge.mirror == Cartridge.Mirror.HORIZONTAL)
                {
                    if (addr >= 0x0000 && addr <= 0x03FF)
                        data = _tblName[0][addr & 0x03FF];
                    else if (addr >= 0x0400 && addr <= 0x07FF)
                        data = _tblName[0][addr & 0x03FF];
                    else if (addr >= 0x0800 && addr <= 0x0BFF)
                        data = _tblName[1][addr & 0x03FF];
                    else if (addr >= 0x0C00 && addr <= 0x0FFF)
                        data = _tblName[1][addr & 0x03FF];
                }
            }
            else if (addr >= 0x3F00 && addr <= 0x3FFF)  // Palette memory range
            {
                // Mask bottom 5 bits
                addr &= 0x001F;

                if (addr == 0x0010) addr = 0x0000;
                if (addr == 0x0014) addr = 0x0004;
                if (addr == 0x0018) addr = 0x0008;
                if (addr == 0x001C) addr = 0x000C;

                data = (byte)(_palette[addr] & (_mask.GrayScale ? 0x30 : 0x3F));
            }

            return data;
        }

        // TODO: I think this should be a write to the _ppuBus... will investigate later
        private void ppuWrite(ushort addr, byte data)
        {
            addr &= ADDR_PPU_MAX;

            // TODO: Eventually loop through ppuBus like we do with the NES bus, attempting to do writes until
            // one "hits"

            if (_cartridge.ppuWrite(addr, data))
            {

            }
            else if (addr <= 0x1FFF)  // Pattern (sprite) memory range
            {
                _tblPattern[(addr & 0x1000) >> 12][addr & 0x0FFF] = data;
            }
            else if (addr >= ADDR_NAMETABLE && addr <= 0x3EFF)  // Nametable memory (VRAM) range
            {
                addr &= 0x0FFF;
                if (_cartridge.mirror == Cartridge.Mirror.VERTICAL)
                {
                    if (addr <= 0x03FF)
                        _tblName[0][addr & 0x03FF] = data;
                    else if (addr >= 0x0400 && addr <= 0x07FF)
                        _tblName[1][addr & 0x03FF] = data;
                    else if (addr >= 0x0800 && addr <= 0x0BFF)
                        _tblName[0][addr & 0x03FF] = data;
                    else if (addr >= 0x0C00 && addr <= 0x0FFF)
                        _tblName[1][addr & 0x03FF] = data;
                }
                else if (_cartridge.mirror == Cartridge.Mirror.HORIZONTAL)
                {
                    if (addr <= 0x03FF)
                        _tblName[0][addr & 0x03FF] = data;
                    else if (addr >= 0x0400 && addr <= 0x07FF)
                        _tblName[0][addr & 0x03FF] = data;
                    else if (addr >= 0x0800 && addr <= 0x0BFF)
                        _tblName[1][addr & 0x03FF] = data;
                    else if (addr >= 0x0C00 && addr <= 0x0FFF)
                        _tblName[1][addr & 0x03FF] = data;
                }
            }
            else if (addr >= 0x3F00 && addr <= 0x3FFF)  // Palette memory range
            {
                // Mask lower 5 bits
                addr &= 0x001F;

                if (addr == 0x0010) addr = 0x0000;
                if (addr == 0x0014) addr = 0x0004;
                if (addr == 0x0018) addr = 0x0008;
                if (addr == 0x001C) addr = 0x000C;

                _palette[addr] = data;
            }
        }

        #endregion // Bus Communications

        public void ConnectCartridge(Cartridge cartridge)
        {
            _cartridge = cartridge;
        }

        private List<PPUCycleNode>.Enumerator _cycleOpItr;

        public override void Clock(ulong clockCounter)
        {
            // As we progress through scanlines and cycles, the PPU is effectively
            // a state machine going through the motions of fetching background 
            // information and sprite information, compositing them into a pixel
            // to be output.

            // Background rendering =======================================================
            if (_cycleOpItr.Current != null && (_cycle == _cycleOpItr.Current.CycleStart))
            {
                // Do the operation for this cycle
                _cycleOpItr.Current.CycleOperation();
                // Prepare for the next operation
                _cycleOpItr.MoveNext();
            }
            // ============================================================================


            // All but 1 of the scanlines is visible to the user. The pre-render scanline at -1, is used
            // to configure the "shifters" for the first visible scanline, 0.
            if (_scanline >= -1 && _scanline < 240)
            {
                // Foreground Rendering ===================================================
                if (_cycle == 257 && _scanline >= 0)
                {
                    // We've reached the end of a visible scanline. It is now time to determine which
                    // sprites are visible on the next scanline, and preload this info into buffers that
                    // we can work with while the scanline scans the row.
                    resetSpriteDataForScanline();

                    evaluateVisibleSpritesForScanline();
                }

                if (_cycle == 340)
                {
                    // Now we're at the very end of the scanline, so prepare the sprite shifters with the 8 or
                    // less selected sprites.
                    for (byte i = 0; i < _spriteCount; i++)
                    {
                        // We need to extract the 8-bit row patterns of the sprite with the correct vertical
                        // offset. The "Sprite Mode" also affects this as the sprites may be 8 or 16 rows high.
                        // Additionally, the sprite can be flipped both vertically and horizontally.
                        byte sprite_pattern_bits_lo, sprite_pattern_bits_hi;
                        ushort sprite_pattern_addr_lo, sprite_pattern_addr_hi;

                        // Determine the memory addresses that contain the byte of pattern data. We only need
                        // the lo pattern address, because the hi pattern address is always offset by 8 from the
                        // lo address.
                        if (!_control.SpriteSize)
                        {
                            sprite_pattern_addr_lo = loadNextSpr8x8TileLSB(i);
                        }
                        else
                        {
                            sprite_pattern_addr_lo = loadNextSpr8x16TileLSB(i);
                        }

                        // Hi bit plane equivalent is always offset by 8 bytes from lo bit plane
                        sprite_pattern_addr_hi = (ushort)(sprite_pattern_addr_lo + 8);

                        // Now we have the address of the sprite patterns, we can read them
                        sprite_pattern_bits_lo = ppuRead(sprite_pattern_addr_lo);
                        sprite_pattern_bits_hi = ppuRead(sprite_pattern_addr_hi);

                        // If the sprite is flipped horizontally, we need to flip the pattern bytes
                        if ((_spriteScanline[i].attribute & 0x40) != 0)
                        {
                            sprite_pattern_bits_lo = sprite_pattern_bits_lo.Flip();
                            sprite_pattern_bits_hi = sprite_pattern_bits_hi.Flip();
                        }

                        // Now we can load the pattern into our sprite shift registers ready for rendering on the next scanline
                        _spriteShifterPatternLo[i] = sprite_pattern_bits_lo;
                        _spriteShifterPatternHi[i] = sprite_pattern_bits_hi;
                    }
                }
            }

            // Composition - We now have background pixel information for this cycle. At this point we are only
            // interested in background
            byte bg_pixel   = 0x00;     // The 2-bit pixel to be rendered
            byte bg_palette = 0x00;     // The 3-bit index of the palette the pixel indexes

            // We only render backgrounds if the PPU is enabled to do so. Note if background rendering is
            // disabled, the pixel and palette combine to form 0x00. This will fall through the color tables to
            // yield the current background color in effect.
            if (_mask.RenderBackground)
            {
                // Handle Pixel Selection by selecting the relevant bit depending upon fine x scrolling. This
                // has the effect of offsetting ALL background rendering by a set number of pixels, permitting
                // smooth scrolling.
                ushort bit_mux = (ushort)(0x8000 >> _fineX);

                // Select Plane pixels by extracting from the shifter
                // at the required location.
                byte p0_pixel = (byte)((_bg_shifterPatternLo & bit_mux) > 0 ? 1 : 0);
                byte p1_pixel = (byte)((_bg_shifterPatternHi & bit_mux) > 0 ? 1 : 0);

                // Combine to form pixel index
                bg_pixel = (byte)((p1_pixel << 1) | p0_pixel);

                byte bg_pal0 = (byte)((_bg_shifterAttribLo & bit_mux) > 0 ? 1 : 0);
                byte bg_pal1 = (byte)((_bg_shifterAttribHi & bit_mux) > 0 ? 1 : 0);
                bg_palette = (byte)((bg_pal1 << 1) | bg_pal0);
            }

            // Foreground ===========================================================
            byte fg_pixel = 0x00;       // The 2-bit pixel to be rendered
            byte fg_palette = 0x00;     // The 3-bit index of the palette the pixel indexes
            byte fg_priority = 0x00;    // A bit of the sprite attribute indicates if it's more important than the background

            if (_mask.RenderSprites)
            {
                // Iterate through all sprites for this scanline. This is to maintain state priority.
                // As soon as we find a non transparent pixel of a sprite, we can abort.
                _spriteZeroBeingRendered = false;

                for (byte i = 0; i < _spriteCount; i++)
                {
                    // Scanline cycle has "collided" with sprite, shifters taking over
                    if (_spriteScanline[i].x == 0)
                    {
                        // Note: Fine X scrolling does not apply to sprites, the game should maintain their relationship
                        // with the background. So, we'll just use the MSB of the shifter.

                        // Determine the pixel value...
                        byte fg_pixel_lo = (byte)((_spriteShifterPatternLo[i] & 0x80) > 0 ? 1 : 0);
                        byte fg_pixel_hi = (byte)((_spriteShifterPatternHi[i] & 0x80) > 0 ? 1 : 0);
                        fg_pixel = (byte)((fg_pixel_hi << 1) | fg_pixel_lo);

                        // Extract the palette from the bottom 2 bits. Recall that foreground palettes are the latter 4
                        // in the palette memory.
                        fg_palette = (byte)((_spriteScanline[i].attribute & 0x03) + 0x04);
                        fg_priority = (byte)((_spriteScanline[i].attribute & 0x20) == 0 ? 1 : 0);

                        // If pixel is not transparent, we render it and don't bother checking the rest because the earlier
                        // sprites in the list are higher priority
                        if (fg_pixel != 0)
                        {
                            // is this sprite 0?
                            if (i == 0)
                            {
                                _spriteZeroBeingRendered = true;
                            }

                            break;
                        }
                    }
                }
            }

            // Now we have a background pixel and a foreground pixel. They need to be combined. It is possible for the sprites
            // to go behind background tiles that are not "transparent", yet another neat trick of the PPU that adds complexity

            byte pixel   = 0x00;    // The FINAL FINAL pixel
            byte palette = 0x00;    // The FINAL FINAL palette

            if (bg_pixel == 0 && fg_pixel == 0)
            {
                // The background AND foreground pixels are transparent, so draw "background" color
                // (which is 0x00 for both, so do nothing)
            }
            else if (bg_pixel == 0 && fg_pixel > 0)
            {
                // The background pixel is transparent
                // The foreground pixel is visible
                // Foreground wins!
                pixel = fg_pixel;
                palette = fg_palette;
            }
            else if (bg_pixel > 0 && fg_pixel == 0)
            {
                // The background pixel is visible
                // The foreground pixel is transparent
                // Background wins!
                pixel = bg_pixel;
                palette = bg_palette;
            }
            else if (bg_pixel > 0 && fg_pixel > 0)
            {
                // The background pixel is visible
                // The foreground pixel is visible
                // Hmmm...
                if (fg_priority != 0)
                {
                    // Foreground cheats and wins!
                    pixel = fg_pixel;
                    palette = fg_palette;
                }
                else
                {
                    // Background steals the pixel!
                    pixel = bg_pixel;
                    palette = bg_palette;
                }

                // Sprite 0 hit detection
                if (_spriteZeroHitPossible && _spriteZeroBeingRendered)
                {
                    // Sprite 0 is a collision between foreground and background so they must both be enabled
                    if (_mask.RenderBackground && _mask.RenderSprites)
                    {
                        // The left edge of the screen has specific switches to control its appearance. This is used to
                        // smooth inconsistencies when scrolling (since sprite's x coord must be >= 0)
                        if (!(_mask.RenderBackgroundLeft || _mask.RenderSpritesLeft))
                        {
                            if (_cycle >= 9 && _cycle < 258)
                            {
                                _status.SpriteZeroHit = true;
                            }
                        }
                        else if (_cycle >= 1 && _cycle < 258)
                        {
                            _status.SpriteZeroHit = true;
                        }
                    }
                }
            }

            // Now we have a final pixel color, and a palette for this cycle of the current scanline. Let's at
            // long last, draw it
            if ((_cycle - 1) >= 0 && (_scanline >= 0 && _scanline <= 239))
                _screen.SetPixel((uint)(_cycle - 1), (ushort)_scanline, GetColorFromPaletteRam(palette, pixel));

            advanceCycle();
        }

        public Pixel GetColorFromPaletteRam(byte palette, byte pixel)
        {
            // This is a convenience function that takes a specified palette and pixel
            // index and returns the appropriate screen colour.
            // "0x3F00"       - Offset into PPU addressable range where palettes are stored
            // "palette << 2" - Each palette is 4 bytes in size
            // "pixel"        - Each pixel index is either 0, 1, 2 or 3
            // "& 0x3F"       - Stops us reading beyond the bounds of the palScreen array
            return _palScreen[ppuRead((ushort)(ADDR_PALETTE + (palette << 2) + pixel)) & MAX_PALETTE];

            // Note: We dont access _palette directly here, instead we know that ppuRead()
            // will map the address onto the seperate small RAM attached to the PPU bus.
        }

        #region Scanline/Cycle operations

        private void advanceCycle()
        {
            _cycle++;

            if (_cycle >= 341)
            {
                _cycle = 0;
                _scanline++;
                if (_scanline >= 262)
                {
                    _scanline = 0;
                    FrameComplete = true;
                }
                _cycleOpItr = _cycleOperations[_scanline].GetEnumerator();
                _cycleOpItr.MoveNext();
            }
        }

        /// <summary>
        /// Increment the background tile "pointer" one tile/column horizontally
        /// </summary>
        private void incrementScrollX()
        {
            // Note: pixel perfect scrolling horizontally is handled by the 
            // data shifters. Here we are operating in the spatial domain of 
            // tiles, 8x8 pixel blocks.

            // Only if rendering is enabled
            if (_mask.RenderBackground || _mask.RenderSprites)
            {
                // A single name table is 32x30 tiles. As we increment horizontally we may cross
                // into a neighboring nametable, or wrap around to a neighboring nametable
                if (_vram_addr.CoarseX == 31)
                {
                    // Leaving nametable so wrap address around
                    _vram_addr.CoarseX = 0;
                    // Flip target nametable bit
                    _vram_addr.NameTableX = !_vram_addr.NameTableX;
                }
                else
                {
                    // Staying in current nametable, so just increment
                    _vram_addr.CoarseX++;
                }
            }
        }

        private void incrementScrollY()
        {
            // Incrementing vertically is more complicated. The visible nametable is 32x30 tiles, but in
            // memory there is enough room for 32x32 tiles. The bottom two rows of tiles are in fact not
            // tiles at all, they contain the "attribute" information for the entire table. This is
            // information that describes which palettes are used for different regions of the nametable.

            // In addition, the NES doesnt scroll vertically in chunks of 8 pixels i.e. the height of a
            // tile, it can perform fine scrolling by using the fine_y component of the register. This
            // means an increment in Y first adjusts the fine offset, but may need to adjust the whole
            // row offset, since fine_y is a value 0 to 7, and a row is 8 pixels high

            // Only if rendering is enabled
            if (_mask.RenderBackground || _mask.RenderSprites)
            {
                // If possible, just increment the fine y offset
                if (_vram_addr.FineY < 7)
                {
                    _vram_addr.FineY++;
                    // Whew! That was easy...
                }
                else
                {
                    // If we have gone beyond the height of a row, we need to increment the row,
                    // potentially wrapping into neighbouring vertical nametables. Dont forget however,
                    // the bottom two rows do not contain tile information. The coarse y offset is used
                    // to identify which row of the nametable we want, and the fine y offset is the
                    // specific "scanline"

                    // Reset fine y offset
                    _vram_addr.FineY = 0;

                    // Check if we need to swap vertical nametable targets
                    if (_vram_addr.CoarseY == 29)
                    {
                        // We do, so reset coarse y offset
                        _vram_addr.CoarseY = 0;
                        // and flip the target nametable bit
                        _vram_addr.NameTableY = !_vram_addr.NameTableY;
                    }
                    else if (_vram_addr.CoarseY == 31)
                    {
                        // In case the pointer is in the attribute memory, we just wrap around the current
                        // nametable
                        _vram_addr.CoarseY = 0;
                    }
                    else
                    {
                        // None of the above boundary/wrapping conditions apply so just increment the
                        // coarse y offset
                        _vram_addr.CoarseY++;
                    }
                }
            }
        }

        /// <summary>
        /// Transfer the temporarily stored horizontal nametable access information into the "pointer". Note
        /// that fine x scrolling is not part of the "pointer" addressing mechanism.
        /// </summary>
        private void transferAddressX()
        {
            // Only if rendering is enabled
            if (_mask.RenderBackground || _mask.RenderSprites)
            {
                _vram_addr.NameTableX   = _tram_addr.NameTableX;
                _vram_addr.CoarseX      = _tram_addr.CoarseX;
            }
        }

        /// <summary>
        /// Transfer the temporarily stored vertical nametable access information into the "pointer". Note
        /// that fine y scrolling is part of the "pointer" addressing mechanism.
        /// </summary>
        private void transferAddressY()
        {
            // Only if rendering is enabled
            if (_mask.RenderBackground || _mask.RenderSprites)
            {
                _vram_addr.FineY        = _tram_addr.FineY;
                _vram_addr.NameTableY   = _tram_addr.NameTableY;
                _vram_addr.CoarseY      = _tram_addr.CoarseY;
            }
        }

        /// <summary>
        /// Prime the "in-effect" background tile shifters ready for outputting next 8 pixels in scanline.
        /// </summary>
        private void loadBackgroundShifters()
        {
            // Each PPU update we calculate one pixel. These shifters shift 1 bit along feeding the pixel
            // compositor with the binary information it needs. Its 16 bits wide, because the top 8 bits
            // are the current 8 pixels being drawn and the bottom 8 bits are the next 8 pixels to be drawn.
            // Naturally this means the required bit is always the MSB of the shifter. However, "fine x"
            // scrolling plays a part in this too, which is seen later, so in fact we can choose any one of
            // the top 8 bits.
            _bg_shifterPatternLo = (ushort)((_bg_shifterPatternLo & 0xFF00) | _bg_nextTileLSB);
            _bg_shifterPatternHi = (ushort)((_bg_shifterPatternHi & 0xFF00) | _bg_nextTileMSB);

            // Attribute bits do not change per pixel, rather they change every 8 pixels but are synchronised
            // with the pattern shifters for convenience, so here we take the bottom 2 bits of the attribute
            // word which represent which palette is being used for the current 8 pixels and the next 8 pixels,
            // and "inflate" them to 8 bit words.
            _bg_shifterAttribLo = (ushort)((_bg_shifterAttribLo & 0xFF00) | (_bg_nextTileAttrib.TestBit(0) ? 0xFF : 0x00));
            _bg_shifterAttribHi = (ushort)((_bg_shifterAttribHi & 0xFF00) | (_bg_nextTileAttrib.TestBit(1) ? 0xFF : 0x00));
        }

        /// <summary>
        /// Every cycle the shifters storing pattern and attribute information shift their contents by 1 bit.
        /// This is because every cycle, the output progresses by 1 pixel. This means relatively, the state of
        /// the shifter is in sync with the pixels being drawn for that 8 pixel section of the scanline.
        /// </summary>
        private void updateShifters()
        {
            if (_mask.RenderBackground)
            {
                // Shifting background tile pattern row
                _bg_shifterPatternLo <<= 1;
                _bg_shifterPatternHi <<= 1;

                // Shift palette attributes by 1
                _bg_shifterAttribLo <<= 1;
                _bg_shifterAttribHi <<= 1;
            }

            if (_mask.RenderSprites && _cycle >= 1 && _cycle < 258)
            {
                for (int i = 0; i < _spriteCount; i++)
                {
                    if (_spriteScanline[i].x > 0)
                    {
                        _spriteScanline[i].x--;
                    }
                    else
                    {
                        _spriteShifterPatternLo[i] <<= 1;
                        _spriteShifterPatternHi[i] <<= 1;
                    }
                }
            }
        }

        private void skipCycle()
        {
            _cycle = 0;
            _scanline = 0;
            FrameComplete = true;
            _cycleOpItr = _cycleOperations[_scanline].GetEnumerator();
            _cycleOpItr.MoveNext();

            fetchNextBGTileId();
        }

        private void noOp()
        { }

        private void fetchNextBGTileId()
        {
            // Fetch the next background tile ID
            // "(vram_addr.reg & 0x0FFF)" : Mask to 12 bits that are relevant
            // "| 0x2000"                 : Offset into nametable space on PPU address bus
            _bg_nextTileId = ppuRead((ushort)(ADDR_NAMETABLE | (_vram_addr.reg & 0x0FFF)));
        }

        private void loadNextBGTileId()
        {
            updateShifters();

            // Load the current background tile pattern and attributes into the "shifter"
            loadBackgroundShifters();

            fetchNextBGTileId();
        }

        private void loadNextBGTileAttrib()
        {
            updateShifters();

            // Fetch the next background tile attribute.

            // Recall that each nametable has two rows of cells that are not tile information, instead
            // they represent the attribute information that indicates which palettes are applied to
            // which area on the screen. Importantly (and frustratingly) there is not a 1 to 1
            // correspondance between background tile and palette. Two rows of tile data holds
            // 64 attributes. Therfore we can assume that the attributes affect 8x8 zones on the screen
            // for that nametable. Given a working resolution of 256x240, we can further assume that each
            // zone is 32x32 pixels in screen space, or 4x4 tiles. Four system palettes are allocated
            // to background rendering, so a palette can be specified using just 2 bits. The attribute
            // byte therefore can specify 4 distinct palettes. Therefore we can even further assume
            // that a single palette is applied to a 2x2 tile combination of the 4x4 tile zone. The
            // very fact that background tiles "share" a palette locally is the reason why in some
            // games you see distortion in the colours at screen edges.

            // As before when choosing the tile ID, we can use the bottom 12 bits of the loopy register,
            // but we need to make the implementation "coarser" because instead of a specific tile,
            // we want the attribute byte for a group of 4x4 tiles, or in other words, we divide our
            // 32x32 address by 4 to give us an equivalent 8x8 address, and we offset this address
            // into the attribute section of the target nametable.

            // Reconstruct the 12 bit loopy address into an offset into the
            // attribute memory

            // "(vram_addr.coarse_x >> 2)"        : integer divide coarse x by 4, 
            //                                      from 5 bits to 3 bits
            // "((vram_addr.coarse_y >> 2) << 3)" : integer divide coarse y by 4, 
            //                                      from 5 bits to 3 bits,
            //                                      shift to make room for coarse x

            // Result so far: YX00 00yy yxxx

            // All attribute memory begins at 0x03C0 within a nametable, so OR with
            // result to select target nametable, and attribute byte offset. Finally
            // OR with 0x2000 to offset into nametable address space on PPU bus.
            _bg_nextTileAttrib = ppuRead((ushort)(0x23C0 | ((_vram_addr.NameTableY ? 1 : 0) << 11)
                                                         | ((_vram_addr.NameTableX ? 1 : 0) << 10)
                                                         | ((_vram_addr.CoarseY >> 2) << 3)
                                                         | (_vram_addr.CoarseX >> 2)));
            // We've read the correct attribute byte for a specified address, but the byte itself is
            // broken down further into the 2x2 tile groups in the 4x4 attribute zone.

            // The attribute byte is assembled thus: BR(76) BL(54) TR(32) TL(10)
            //
            // +----+----+			    +----+----+
            // | TL | TR |			    | ID | ID |
            // +----+----+ where TL =   +----+----+
            // | BL | BR |			    | ID | ID |
            // +----+----+			    +----+----+
            //
            // Since we know we can access a tile directly from the 12 bit address, we can analyze
            // the bottom bits of the coarse coordinates to provide us with the correct offset into
            // the 8-bit word, to yield the 2 bits we are actually interested in which specifies the
            // palette for the 2x2 group of tiles. We know if "coarse y % 4" < 2 we are in the top
            // half else bottom half. Likewise if "coarse x % 4" < 2 we are in the left half else
            // right half. Ultimately we want the bottom two bits of our attribute word to be the
            // palette selected. So shift as required...
            if (_vram_addr.CoarseY.TestBit(1)) _bg_nextTileAttrib >>= 4;
            if (_vram_addr.CoarseX.TestBit(1)) _bg_nextTileAttrib >>= 2;
            _bg_nextTileAttrib &= 0x03;
        }

        private void loadNextBGTileLSB()
        {
            updateShifters();

            // Fetch the next background tile LSB bit plane from the pattern memory. The Tile ID has
            // been read from the nametable. We will use this id to index into the pattern memory to
            // find the correct sprite (assuming the sprites lie on 8x8 pixel boundaries in that memory,
            // which they do even though 8x16 sprites exist, as background tiles are always 8x8).
            //
            // Since the sprites are effectively 1 bit deep, but 8 pixels wide, we can represent a
            // whole sprite row as a single byte, so offsetting into the pattern memory is easy. In
            // total there is 8KB so we need a 13 bit address.

            // "(control.pattern_background << 12)"  : the pattern memory selector from control
            //                                         register, either 0K or 4K offset
            // "((uint16_t)bg_next_tile_id << 4)"    : the tile id multiplied by 16, as
            //                                         2 lots of 8 rows of 8 bit pixels
            // "(vram_addr.fine_y)"                  : Offset into which row based on
            //                                         vertical scroll offset
            // "+ 0"                                 : Mental clarity for plane offset
            // Note: No PPU address bus offset required as it starts at 0x0000
            _bg_nextTileLSB = ppuRead((ushort)(((_control.PatternBackground ? 1 : 0) << 12)
                                              + (_bg_nextTileId << 4)
                                              + (_vram_addr.FineY + 0)));
        }

        private void loadNextBGTileMSB()
        {
            updateShifters();

            // Fetch the next background tile MSB bit plane from the pattern memory. This is the same
            // as above, but has a +8 offset to select the next bit plane
            _bg_nextTileMSB = ppuRead((ushort)(((_control.PatternBackground ? 1 : 0) << 12)
                                              + (_bg_nextTileId << 4)
                                              + (_vram_addr.FineY + 8)));
        }

        private void advanceBGTileX()
        {
            updateShifters();

            // Increment the background tile "pointer" to the next tile horizontally in the nametable
            // memory. Note this may cross nametable boundaries which is a little complex, but
            // essential to implement scrolling
            incrementScrollX();
        }

        private void advanceBGTileY()
        {
            updateShifters();

            incrementScrollX(); // scrolling down seems to also scroll right??
            incrementScrollY();
        }

        private void resetBGForNextScanLine()
        {
            loadBackgroundShifters();
            transferAddressX();
        }

        private void startVerticalBlank()
        {
            // Effectively end of frame, so set vertical blank flag
            _status.VerticalBlank = true;

            // If the control register tells us to emit a NMI when entering vertical blanking period,
            // do it! The CPU will be informed that rendering is complete so it can perform operations
            // with the PPU knowing it won't produce visible artifacts.
            if (_control.EnableNMI)
                this.RaiseInterrupt?.Invoke(this, new InterruptEventArgs(InterruptType.NMI));
        }

        private void clearVerticalBlank()
        {
            // Effectively start of new frame, so clear vertical blank flag
            _status.VerticalBlank = false;

            // Clear sprite overflow flag
            _status.SpriteOverflow = false;

            // Clear sprite 0 hit flag
            _status.SpriteZeroHit = false;

            // Clear shifters
            for (int i = 0; i < 8; i++)
            {
                _spriteShifterPatternLo[i] = 0;
                _spriteShifterPatternHi[i] = 0;
            }

            // We also need to start pre-loading first scanline here
            loadNextBGTileId();
        }

        private List<PPUCycleNode> createCycleNodesForSingleBGTile(short tileNum, bool advanceTileRight)
        {
            Action advanceTileAction;

            if (advanceTileRight)
                advanceTileAction = advanceBGTileX;
            else
                advanceTileAction = advanceBGTileY;

            short offset = (short)(tileNum * 8);
            List<PPUCycleNode> fetchBGTileSeq = new List<PPUCycleNode>(new PPUCycleNode[]
            {
                    new PPUCycleNode((short)(offset + 1), loadNextBGTileId),     new PPUCycleNode((short)(offset + 2), updateShifters),
                    new PPUCycleNode((short)(offset + 3), loadNextBGTileAttrib), new PPUCycleNode((short)(offset + 4), updateShifters),
                    new PPUCycleNode((short)(offset + 5), loadNextBGTileLSB),    new PPUCycleNode((short)(offset + 6), updateShifters),
                    new PPUCycleNode((short)(offset + 7), loadNextBGTileMSB),    new PPUCycleNode((short)(offset + 8), advanceTileAction)
            });

            return fetchBGTileSeq;
        }

        private void resetSpriteDataForScanline()
        {
            foreach (var sl in _spriteScanline)
                sl.Fill(0xFF);

            _spriteCount = 0;

            for (byte i = 0; i < 8; i++)
            {
                _spriteShifterPatternLo[i] = 0;
                _spriteShifterPatternHi[i] = 0;
            }
        }

        private void evaluateVisibleSpritesForScanline()
        {
            // Evaluate which sprites are visible in the next scanline. We need to iterate through
            // the OAM until we have found 8 sprites that have Y-positions and heights that are
            // within vertical range of the next scanline. Once we have found 8 or exhausted the OAM
            // we stop.
            byte OAMEntry = 0;

            // New set of sprites. Sprite zero may not exist in the new set, so clear this flag.
            _spriteZeroHitPossible = false;

            while (OAMEntry < 64 && _spriteCount < 9)
            {
                short diff = (short)(_scanline - OAM[OAMEntry].y);

                // If the difference is positive then the scanline is at least at the same height
                // as the sprite, so check if it resides in the sprite vertically depending on the
                // current "sprite height mode"
                if (diff >= 0 && diff < (_control.SpriteSize ? 16 : 8))
                {
                    // Sprite is visible, so copy the attribute entry over to our scanline sprite cache.
                    // I've added < 8 here to guard the array being written to.
                    if (_spriteCount < 8)
                    {
                        // Is this sprite zero?
                        if (OAMEntry == 0)
                        {
                            // Yup, so it may trigger a sprite zero hit when drawn
                            _spriteZeroHitPossible = true;
                        }

                        _spriteScanline[_spriteCount] = OAM[OAMEntry];
                        _spriteCount++;
                    }
                }

                OAMEntry++;
            }

            // Set sprite overflow flag
            _status.SpriteOverflow = (_spriteCount > 8);
        }

        private ushort loadNextSpr8x8TileLSB(byte i)
        {
            ushort sprite_pattern_addr_lo;

            ushort cellRow = (ushort)(_scanline - _spriteScanline[i].y);
            cellRow = (ushort)((_spriteScanline[i].attribute & 0x80) == 0 ? cellRow : (7 - cellRow));

            sprite_pattern_addr_lo = (ushort)(
                 ((_control.PatternSprite ? 1 : 0) << 12)         // Which pattern table? 0KB or 4KB offset
                | (_spriteScanline[i].id << 4)          // Which cell? Tile ID * 16 (16B per tile)
                | cellRow);                             // Which row in cell?

            return sprite_pattern_addr_lo;
        }

        private ushort loadNextSpr8x16TileLSB(byte i)
        {
            ushort sprite_pattern_addr_lo;

            // 8x16 sprite mode - the sprite attribute determines the pattern table
            ushort cellRow  = (ushort)((_scanline - _spriteScanline[i].y) & 0x07);
            cellRow         = (ushort)((_spriteScanline[i].attribute & 0x80) == 0 ? cellRow : 7 - cellRow);
            int topHalf     = (byte)((_scanline - _spriteScanline[i].y) < 8 ? 0 : 1);

            sprite_pattern_addr_lo = (ushort)(
                  ((_spriteScanline[i].id & 0x01) << 12)                // Which pattern table? 0KB or 4KB offset
                | (((_spriteScanline[i].id & 0xFE) + topHalf) << 4)     // Which cell? Tile ID * 16 (16B per tile)
                | cellRow);                                             // Which row in cell?

            return sprite_pattern_addr_lo;
        }

        #endregion // Scanline/Cycle operations

        private void buildPalette()
        {
            _palScreen[0x00] = new Pixel(84, 84, 84);
            _palScreen[0x01] = new Pixel(0, 30, 116);
            _palScreen[0x02] = new Pixel(8, 16, 144);
            _palScreen[0x03] = new Pixel(48, 0, 136);
            _palScreen[0x04] = new Pixel(68, 0, 100);
            _palScreen[0x05] = new Pixel(92, 0, 48);
            _palScreen[0x06] = new Pixel(84, 4, 0);
            _palScreen[0x07] = new Pixel(60, 24, 0);
            _palScreen[0x08] = new Pixel(32, 42, 0);
            _palScreen[0x09] = new Pixel(8, 58, 0);
            _palScreen[0x0A] = new Pixel(0, 64, 0);
            _palScreen[0x0B] = new Pixel(0, 60, 0);
            _palScreen[0x0C] = new Pixel(0, 50, 60);
            _palScreen[0x0D] = new Pixel(0, 0, 0);
            _palScreen[0x0E] = new Pixel(0, 0, 0);
            _palScreen[0x0F] = new Pixel(0, 0, 0);

            _palScreen[0x10] = new Pixel(152, 150, 152);
            _palScreen[0x11] = new Pixel(8, 76, 196);
            _palScreen[0x12] = new Pixel(48, 50, 236);
            _palScreen[0x13] = new Pixel(92, 30, 228);
            _palScreen[0x14] = new Pixel(136, 20, 176);
            _palScreen[0x15] = new Pixel(160, 20, 100);
            _palScreen[0x16] = new Pixel(152, 34, 32);
            _palScreen[0x17] = new Pixel(120, 60, 0);
            _palScreen[0x18] = new Pixel(84, 90, 0);
            _palScreen[0x19] = new Pixel(40, 114, 0);
            _palScreen[0x1A] = new Pixel(8, 124, 0);
            _palScreen[0x1B] = new Pixel(0, 118, 40);
            _palScreen[0x1C] = new Pixel(0, 102, 120);
            _palScreen[0x1D] = new Pixel(0, 0, 0);
            _palScreen[0x1E] = new Pixel(0, 0, 0);
            _palScreen[0x1F] = new Pixel(0, 0, 0);

            _palScreen[0x20] = new Pixel(236, 238, 236);
            _palScreen[0x21] = new Pixel(76, 154, 236);
            _palScreen[0x22] = new Pixel(120, 124, 236);
            _palScreen[0x23] = new Pixel(176, 98, 236);
            _palScreen[0x24] = new Pixel(228, 84, 236);
            _palScreen[0x25] = new Pixel(236, 88, 180);
            _palScreen[0x26] = new Pixel(236, 106, 100);
            _palScreen[0x27] = new Pixel(212, 136, 32);
            _palScreen[0x28] = new Pixel(160, 170, 0);
            _palScreen[0x29] = new Pixel(116, 196, 0);
            _palScreen[0x2A] = new Pixel(76, 208, 32);
            _palScreen[0x2B] = new Pixel(56, 204, 108);
            _palScreen[0x2C] = new Pixel(56, 180, 204);
            _palScreen[0x2D] = new Pixel(60, 60, 60);
            _palScreen[0x2E] = new Pixel(0, 0, 0);
            _palScreen[0x2F] = new Pixel(0, 0, 0);

            _palScreen[0x30] = new Pixel(236, 238, 236);
            _palScreen[0x31] = new Pixel(168, 204, 236);
            _palScreen[0x32] = new Pixel(188, 188, 236);
            _palScreen[0x33] = new Pixel(212, 178, 236);
            _palScreen[0x34] = new Pixel(236, 174, 236);
            _palScreen[0x35] = new Pixel(236, 174, 212);
            _palScreen[0x36] = new Pixel(236, 180, 176);
            _palScreen[0x37] = new Pixel(228, 196, 144);
            _palScreen[0x38] = new Pixel(204, 210, 120);
            _palScreen[0x39] = new Pixel(180, 222, 120);
            _palScreen[0x3A] = new Pixel(168, 226, 144);
            _palScreen[0x3B] = new Pixel(152, 226, 180);
            _palScreen[0x3C] = new Pixel(160, 214, 228);
            _palScreen[0x3D] = new Pixel(160, 162, 160);
            _palScreen[0x3E] = new Pixel(0, 0, 0);
            _palScreen[0x3F] = new Pixel(0, 0, 0);
        }

        private void buildCycleOperations()
        {
            // Scanlines
            _cycleOperations = new List<PPUCycleNode>[262];

            List<PPUCycleNode> visibleScanlineSequence = new List<PPUCycleNode>();

            // Typical scanline does no-op for first cycle
            visibleScanlineSequence.Add(new PPUCycleNode(0, noOp));
            for (short tile = 0; tile < 32; tile++)
            {
                visibleScanlineSequence.AddRange(createCycleNodesForSingleBGTile(tile, tile != 31));
            }
            visibleScanlineSequence.Add(new PPUCycleNode(257, resetBGForNextScanLine));
            visibleScanlineSequence.Add(new PPUCycleNode(258, noOp));
            // Pre-load first two tiles for next scanline
            visibleScanlineSequence.AddRange(createCycleNodesForSingleBGTile(40, true));
            visibleScanlineSequence.AddRange(createCycleNodesForSingleBGTile(41, true));
            // Add superfluous reads of next BG tile id
            visibleScanlineSequence.Add(new PPUCycleNode(337, fetchNextBGTileId));
            visibleScanlineSequence.Add(new PPUCycleNode(339, fetchNextBGTileId));


            int scanline = 0;

            // Add sequences to cycle ops for visible scanlines
            _cycleOperations[scanline] = new List<PPUCycleNode>(visibleScanlineSequence);
            // Replace first cycle with skip function instead of no-op
            //_cycleOperations[scanline][0] = new PPUCycleNode(0, skipCycle);
            scanline++; // scanline = 1

            for (; scanline < 240; scanline++)
            {
                _cycleOperations[scanline] = new List<PPUCycleNode>(visibleScanlineSequence);
            }

            // Scanline 240 does nothing
            _cycleOperations[scanline] = new List<PPUCycleNode>();
            _cycleOperations[scanline].Add(new PPUCycleNode(0, noOp));
            scanline++; // scanline = 241

            // Scanline 241, cycle 1 sets VBL flag
            _cycleOperations[scanline] = new List<PPUCycleNode>();
            _cycleOperations[scanline].Add(new PPUCycleNode(0, noOp));
            _cycleOperations[scanline].Add(new PPUCycleNode(1, startVerticalBlank));
            _cycleOperations[scanline].Add(new PPUCycleNode(2, noOp));
            scanline++; // scanline = 242

            // Scanlines 242-260 do absolutely nothing. Boy are they lazy!
            for (; scanline < 261; scanline++)
            {
                _cycleOperations[scanline] = new List<PPUCycleNode>();
                _cycleOperations[scanline].Add(new PPUCycleNode(0, noOp));
            }

            // Scanline 261 clears VBL flag and pre-loads first scanline on next frame.
            // Current implementation doesn't quite do this, so I'll leave it out for now.
            _cycleOperations[scanline] = new List<PPUCycleNode>(visibleScanlineSequence);

            // SPECIAL CASE: Replace cycle 1 with clearVerticalBlank
            _cycleOperations[scanline][1] = new PPUCycleNode(1, clearVerticalBlank);

            // Replace last cycle with the skip
             _cycleOperations[scanline][_cycleOperations[scanline].Count - 1] = new PPUCycleNode(339, skipCycle);

            // SPECIAL CASE: Do Y address xfer every cycle from 280-304
            // Find the index of the "no-op" at cycle 258 and jamb these in right after
            int insertionPoint = visibleScanlineSequence.FindIndex((cycleNode) => cycleNode.CycleStart == 258) + 1;
            for (short cycle = 280; cycle < 305; cycle++)
            {
                _cycleOperations[scanline].Insert(insertionPoint, new PPUCycleNode(cycle, transferAddressY));
                insertionPoint++;
            }

            // Useless statement to break on
            scanline = scanline;
        }
    }
}
