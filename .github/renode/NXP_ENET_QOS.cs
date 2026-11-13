//
// Copyright (c) 2010-2026 Antmicro
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt' of the Renode repository.
//
// Renode 1.17.0's SynopsysDWCEthernetQualityOfService as one file, with 4-byte descriptor alignment.
namespace Antmicro.Renode.Peripherals.Network
{
    using System;
    using System.Collections.Generic;
    using System.Net;
    using Antmicro.Renode.Core;
    using Antmicro.Renode.Core.Structure;
    using Antmicro.Renode.Core.Structure.Registers;
    using Antmicro.Renode.Exceptions;
    using Antmicro.Renode.Logging;
    using Antmicro.Renode.Network;
    using Antmicro.Renode.Peripherals.Bus;
    using Antmicro.Renode.Peripherals.Timers;
    using Antmicro.Renode.Time;
    using Antmicro.Renode.Utilities;
    using PacketDotNet;

    public partial class NXP_ENET_QOS : NetworkWithPHY, IMACInterface, IKnownSize, IHasFrequency
    {
        public NXP_ENET_QOS(IMachine machine, ulong systemClockFrequency, IPeripheral cpuContext = null, BusWidth? dmaBusWidth = null, ulong? ptpClockFrequency = null,
            int rxQueueSize = 8192, int txQueueSize = 8192, int dmaChannelCount = 1, string externalMacAddress = null) : base(machine)
        {
            if(dmaBusWidth.HasValue)
            {
                DMABusWidth = dmaBusWidth.Value;
            }
            else
            {
                this.WarningLog("DMA bus width not provided to an instance of {0}, defaulting to {1}", nameof(NXP_ENET_QOS), nameof(BusWidth.Bits32));
                DMABusWidth = BusWidth.Bits32;
            }

            if(dmaChannelCount < 1 || dmaChannelCount > MaxDMAChannels)
            {
                throw new ConstructionException($"Invalid DMA channel count {dmaChannelCount}. Expected value between 1 and {MaxDMAChannels}");
            }

            if(externalMacAddress != null)
            {
                this.externalMacAddress = MACAddress.Parse(externalMacAddress);
            }

            ValidateQueueSize(rxQueueSize, nameof(rxQueueSize));
            RxQueueSize = rxQueueSize;
            ValidateQueueSize(txQueueSize, nameof(txQueueSize));
            TxQueueSize = txQueueSize;

            IRQ = new GPIO();
            MAC = EmulationManager.Instance.CurrentEmulation.MACRepository.GenerateUniqueMAC();
            MAC1 = EmulationManager.Instance.CurrentEmulation.MACRepository.GenerateUniqueMAC();
            Bus = machine.GetSystemBus(this);
            if(cpuContext != null)
            {
                this.WarningLog("Providing {0} to instances of {1} is deprecated and has no effect", nameof(cpuContext), nameof(NXP_ENET_QOS));
            }

            this.ptpClockFrequency = ptpClockFrequency ?? systemClockFrequency;
            timestampSubsecondTimer = new LimitTimer(machine.ClockSource, this.ptpClockFrequency, this, "Timestamp timer", BinarySubsecondRollover, Direction.Ascending, eventEnabled: true);
            timestampSubsecondTimer.LimitReached += () => timestampSecondTimer += 1;

            dmaChannels = new DMAChannel[dmaChannelCount];
            for(var i = 0; i < dmaChannels.Length; i++)
            {
                dmaChannels[i] = new DMAChannel(this, i, systemClockFrequency, SeparateDMAInterrupts);
            }

            rxIpcPacketCounterInterruptEnable = new IFlagRegisterField[NumberOfIpcCounters];
            rxIpcByteCounterInterruptEnable = new IFlagRegisterField[NumberOfIpcCounters];
            rxIpcPacketCounterInterrupt = new IFlagRegisterField[NumberOfIpcCounters];
            rxIpcByteCounterInterrupt = new IFlagRegisterField[NumberOfIpcCounters];
            rxIpcPacketCounter = new IValueRegisterField[NumberOfIpcCounters];
            rxIpcByteCounter = new IValueRegisterField[NumberOfIpcCounters];
            macAndMmcRegisters = new DoubleWordRegisterCollection(this, CreateRegisterMap());
            mtlRegisters = new DoubleWordRegisterCollection(this, CreateMTLRegisterMap());
            dmaRegisters = new DoubleWordRegisterCollection(this, CreateDMARegisterMap());
        }

        public void ReceiveFrame(EthernetFrame frame)
        {
            if(!rxEnable.Value)
            {
                this.Log(LogLevel.Debug, "Receive: Dropping frame {0}", frame);
                return;
            }

            var channelCount = dmaChannels.Length;

            // If only one channel is specified packetDuplicationControl and dmaChannelSelect fields will be null, so
            // route the packet to the only available channel and return early
            if(channelCount == 1)
            {
                dmaChannels[0].ReceiveFrame(frame);
                return;
            }

            if(packetDuplicationControl.Value)
            {
                // With packet duplication enabled each set bit in the DCS field encodes a DMA channel that should receive the frame
                BitHelper.ForeachActiveBit(dmaChannelSelect.Value, bit => dmaChannels[bit].ReceiveFrame(frame));
            }
            else
            {
                // DCS encodes a DMA channel number when packet duplication control is disabled
                var channel = (int)dmaChannelSelect.Value;
                if(channel >= channelCount)
                {
                    this.WarningLog("DMA channel number {0} is not in range of [0, {1}). Dropping packet", channel, channelCount);
                    return;
                }

                dmaChannels[channel].ReceiveFrame(frame);
            }
        }

        public override void Reset()
        {
            ResetRegisters();
            SoftwareReset();
            timestampSecondTimer = 0;
            timestampSubsecondTimer.Reset();
            UpdateInterrupts();
        }

        public virtual long Size => 0xC00;

        [DefaultInterrupt]
        public GPIO IRQ { get; }

        public MACAddress MAC { get; set; }

        public MACAddress MAC0 => MAC;

        public MACAddress MAC1 { get; set; }

        public byte IPVersion { get; set; } = 0x42;

        public byte UserIPVersion { get; set; } = 0x31;

        public AddressWidth Address64
        {
            get => address64Value;
            set
            {
                if(!Enum.IsDefined(typeof(AddressWidth), value))
                {
                    throw new RecoverableException($"Invalid value for {nameof(Address64)}: {value}");
                }
                address64Value = value;
            }
        }

        public PhyInterface ActivePhy
        {
            get => activePhyValue;
            set
            {
                if(!Enum.IsDefined(typeof(PhyInterface), value))
                {
                    throw new RecoverableException($"Invalid value for {nameof(ActivePhy)}: {value}");
                }
                activePhyValue = value;
            }
        }

        public ulong Frequency
        {
            get => ptpClockFrequency;

            set => ptpClockFrequency = value;
        }

        public event Action<EthernetFrame> FrameReady;

        // Configuration options for derived classes
        protected BusWidth DMABusWidth { get; private set; }

        protected int RxQueueSize { get; private set; }

        protected int TxQueueSize { get; private set; }

        protected virtual bool SeparateDMAInterrupts => false;

        private void SoftwareReset()
        {
            foreach(var channel in dmaChannels)
            {
                channel.Reset();
            }
            UpdateInterrupts();
        }

        private void SendFrame(EthernetFrame frame)
        {
            if(loopbackEnabled.Value)
            {
                ReceiveFrame(frame);
            }
            else
            {
                FrameReady?.Invoke(frame);
            }

            IncrementPacketCounter(txGoodPacketCounter, txGoodPacketCounterInterrupt);
            IncrementPacketCounter(txPacketCounter, txPacketCounterInterrupt);
            // one added to account for Start of Frame Delimiter (SFD)
            var byteCount = 1 + (uint)frame.Bytes.Length;
            IncrementByteCounter(txByteCounter, txByteCounterInterrupt, byteCount);
            IncrementByteCounter(txGoodByteCounter, txGoodByteCounterInterrupt, byteCount);
            if(frame.DestinationMAC.IsBroadcast)
            {
                IncrementPacketCounter(txBroadcastPacketCounter, txBroadcastPacketCounterInterrupt);
            }
            else if(frame.DestinationMAC.IsMulticast)
            {
                IncrementPacketCounter(txMulticastPacketCounter, txMulticastPacketCounterInterrupt);
            }
            else
            {
                IncrementPacketCounter(txUnicastPacketCounter, txUnicastPacketCounterInterrupt);
            }
#if DEBUG
            this.Log(LogLevel.Noisy, "Transmission: frame {0}", Misc.PrettyPrintCollectionHex(frame.Bytes));
            this.Log(LogLevel.Debug, "Transmission: frame {0}", frame);
#endif
        }

        private void UpdateRxCounters(EthernetFrame frame, RxDescriptor.NormalWriteBackDescriptor writeBackStructure)
        {
            if(writeBackStructure.CrcError)
            {
                IncrementPacketCounter(rxCrcErrorPacketCounter, rxCrcErrorPacketCounterInterrupt);
            }
            IncrementPacketCounter(rxPacketCounter, rxPacketCounterInterrupt);
            // byte count excludes preamble, one added to account for Start of Frame Delimiter (SFD)
            var byteCount = 1 + writeBackStructure.PacketLength;
            IncrementByteCounter(rxByteCounter, rxByteCounterInterrupt, byteCount);

            var isRuntPacket = writeBackStructure.PacketLength <= EthernetFrame.RuntPacketMaximumSize;
            var lengthOutOfRange = frame.Length > EthernetFrame.MaximumFrameSize;
            var isNontypePacket = (writeBackStructure.LengthTypeField != PacketKind.TypePacket);
            var lengthMismatch = (frame.Length != writeBackStructure.PacketLength);

            var lengthError = isNontypePacket && lengthMismatch;
            var outOfRange = isNontypePacket && lengthOutOfRange;
            if(writeBackStructure.CrcError || isRuntPacket || lengthError || outOfRange)
            {
                return;
            }

            IncrementByteCounter(rxGoodByteCounter, rxGoodByteCounterInterrupt, byteCount);
            if(frame.DestinationMAC.IsBroadcast)
            {
                IncrementPacketCounter(rxBroadcastPacketCounter, rxBroadcastPacketCounterInterrupt);
            }
            else if(frame.DestinationMAC.IsMulticast)
            {
                IncrementPacketCounter(rxMulticastPacketCounter, rxMulticastPacketCounterInterrupt);
            }
            else
            {
                IncrementPacketCounter(rxUnicastPacketCounter, rxUnicastPacketCounterInterrupt);
            }

            if(writeBackStructure.Ipv4HeaderPresent)
            {
                IncreaseIpcCounter(IpcCounter.IpV4Good, byteCount);
                if(writeBackStructure.IpHeaderError)
                {
                    IncreaseIpcCounter(IpcCounter.IpV4HeaderError, byteCount);
                }
            }
            if(writeBackStructure.Ipv6HeaderPresent)
            {
                IncreaseIpcCounter(IpcCounter.IpV6Good, byteCount);
                if(writeBackStructure.IpHeaderError)
                {
                    IncreaseIpcCounter(IpcCounter.IpV6HeaderError, byteCount);
                }
            }
            if(writeBackStructure.PayloadType == PayloadType.UDP)
            {
                IncreaseIpcCounter(IpcCounter.UdpGood, byteCount);
            }
            else if(writeBackStructure.PayloadType == PayloadType.TCP)
            {
                IncreaseIpcCounter(IpcCounter.TcpGood, byteCount);
            }
            else if(writeBackStructure.PayloadType == PayloadType.ICMP)
            {
                IncreaseIpcCounter(IpcCounter.IcmpGood, byteCount);
            }
        }

        private void IncrementPacketCounter(IValueRegisterField counter, IFlagRegisterField status)
        {
            counter.Value += 1;
            status.Value |= EqualsHalfOrMaximumCounterValue(counter);
        }

        private void IncrementByteCounter(IValueRegisterField counter, IFlagRegisterField status, uint increment)
        {
            status.Value |= WouldExceedHalfOrMaximumCounterValue(counter, increment);
            counter.Value += increment;
        }

        private void IncreaseIpcCounter(IpcCounter type, uint byteCount)
        {
            var index = (int)type;
            IncrementPacketCounter(rxIpcPacketCounter[index], rxIpcPacketCounterInterrupt[index]);
            IncrementByteCounter(rxIpcByteCounter[index], rxIpcByteCounterInterrupt[index], byteCount);
        }

        private bool CheckAnySetIpcCounterInterrupts()
        {
            for(var i = 0; i < NumberOfIpcCounters; i++)
            {
                if((rxIpcPacketCounterInterrupt[i].Value && rxIpcPacketCounterInterruptEnable[i].Value) || (rxIpcByteCounterInterrupt[i].Value && rxIpcByteCounterInterruptEnable[i].Value))
                {
                    return true;
                }
            }
            return false;
        }

        private bool EqualsHalfOrMaximumCounterValue(IValueRegisterField counter)
        {
            return (counter.Value == CounterMaxValue) || (counter.Value == (CounterMaxValue / 2));
        }

        private bool WouldExceedHalfOrMaximumCounterValue(IValueRegisterField counter, uint increment)
        {
            return (counter.Value < CounterMaxValue && counter.Value + increment >= CounterMaxValue)
                || (counter.Value < CounterMaxValue / 2 && counter.Value + increment >= CounterMaxValue / 2);
        }

        private void UpdateInterrupts()
        {
            var dmaInterrupt = false;
            foreach(var channel in dmaChannels)
            {
                dmaInterrupt |= channel.UpdateInterrupts();
            }

            var irq = (ptpMessageTypeInterrupt.Value && ptpMessageTypeInterruptEnable.Value)   ||
                      (lowPowerIdleInterrupt.Value && lowPowerIdleInterruptEnable.Value)       ||
                      (timestampInterrupt.Value && timestampInterruptEnable.Value)             ||
                      dmaInterrupt                                                             ||
                      MMCTxInterruptStatus                                                     ||
                      MMCRxInterruptStatus                                                     ||
                      CheckAnySetIpcCounterInterrupts();

            this.Log(LogLevel.Noisy, "Setting IRQ: {0}", irq);
            IRQ.Set(irq);
        }

        private void GetTimestamp(out ulong seconds, out ulong nanoseconds)
        {
            if(enableTimestamp.Value && Bus.TryGetCurrentCPU(out var cpu))
            {
                cpu.SyncTime();
            }

            seconds = timestampSecondTimer;
            // `SSINC` dictates the accuracy of the subsecond value
            nanoseconds = (timestampSubsecondTimer.Value / timestampSubsecondIncrement.Value) * timestampSubsecondIncrement.Value;
        }

        private void SetTimestamp(ulong seconds, ulong nanoseconds, bool init)
        {
            if(!init)
            {
                GetTimestamp(out var currentSeconds, out var currentNanoseconds);
                if(addOrSubtractTime.Value)
                {
                    currentSeconds -= (1ul << 32) - seconds;
                    if(timestampDigitalOrBinaryRollover.Value)
                    {
                        // When TSCTRLSSR field in the Timestamp control Register is 1 the programmed value must be 10^9 - <subsecond value>
                        currentNanoseconds -= (ulong)Math.Pow(10, 9) - nanoseconds;
                    }
                    else
                    {
                        // When TSCTRLSSR field in the Timestamp control Register is 1 the programmed value must be 2^31 - <subsecond_value>
                        currentNanoseconds -= (1ul << 31) - nanoseconds;
                    }
                }
                else
                {
                    currentSeconds += seconds;
                    currentNanoseconds += nanoseconds;
                }
                seconds = currentSeconds;
                nanoseconds = currentNanoseconds;
            }

            ConfigureTimestampTimer();
            timestampSecondTimer = (uint)seconds;
            timestampSubsecondTimer.Value = nanoseconds;
            if(init)
            {
                timestampSubsecondTimer.Enabled = true;
            }
        }

        private void ConfigureTimestampTimer()
        {
            ulong effectiveFrequency;
            // Check for values that would cause an invalid operation, either:
            // * Timer frequency equal to 0Hz
            // * Division by 0
            if((timestampAddend.Value != 0 || !fineOrCoarseTimestampUpdate.Value) && timestampSubsecondIncrement.Value != 0)
            {
                // Subsecond increment (SSINC) controls by what value the timestamp timer is incremented each clock cycle.
                // This is implemented by multiplying the frequency of Renode's timer by SSINC.
                var subsecondTicksPerSecond = ptpClockFrequency * timestampSubsecondIncrement.Value;
                // In the fine update method subsecond timer is incremented when a 32-bit accumulator overflows while the timestamp addend (TSAR)
                // is added to the accumulator. This creates a frequency divider so it is implemented in this way.
                var timeBetweenTimeSyncs = fineOrCoarseTimestampUpdate.Value ? (1L << 32) / (double)timestampAddend.Value : 1;
                effectiveFrequency = (ulong)Math.Round(subsecondTicksPerSecond / timeBetweenTimeSyncs);
            }
            else
            {
                this.ErrorLog("Register configuration would cause an invalid timestamp timer frequency (SSINC=0x{0:X}, TSAR=0x{1:X}, TSCFUPDT={2}), using the default frequency of {3}Hz",
                    timestampSubsecondIncrement.Value, timestampAddend.Value, fineOrCoarseTimestampUpdate.Value, ptpClockFrequency);
                effectiveFrequency = ptpClockFrequency;
            }
            timestampSubsecondTimer.Frequency = effectiveFrequency;
            this.DebugLog("Effective timestamp timer frequency is {0}Hz", effectiveFrequency);
        }

        private void ValidateQueueSize(int size, string paramName)
        {
            if(!Misc.IsPowerOfTwo((ulong)size) || size < MinimumQueueSize)
            {
                throw new ConstructionException($"{paramName} value has to be a power of 2 and at least {MinimumQueueSize}, got {size}");
            }
        }

        private bool MMCTxInterruptStatus =>
            (txGoodPacketCounterInterrupt.Value && txGoodPacketCounterInterruptEnable.Value) ||
            (txGoodByteCounterInterrupt.Value && txGoodByteCounterInterruptEnable.Value) ||
            (txBroadcastPacketCounterInterrupt.Value && txBroadcastPacketCounterInterruptEnable.Value) ||
            (txMulticastPacketCounterInterrupt.Value && txMulticastPacketCounterInterruptEnable.Value) ||
            (txUnicastPacketCounterInterrupt.Value && txUnicastPacketCounterInterruptEnable.Value) ||
            (txPacketCounterInterrupt.Value && txPacketCounterInterruptEnable.Value) ||
            (txByteCounterInterrupt.Value && txByteCounterInterruptEnable.Value);

        private bool MMCRxInterruptStatus =>
            (rxFifoPacketCounterInterrupt.Value && rxFifoPacketCounterInterruptEnable.Value) ||
            (rxUnicastPacketCounterInterrupt.Value && rxUnicastPacketCounterInterruptEnable.Value) ||
            (rxCrcErrorPacketCounterInterrupt.Value && rxCrcErrorPacketCounterInterruptEnable.Value) ||
            (rxMulticastPacketCounterInterrupt.Value && rxMulticastPacketCounterInterruptEnable.Value) ||
            (rxBroadcastPacketCounterInterrupt.Value && rxBroadcastPacketCounterInterruptEnable.Value) ||
            (rxGoodByteCounterInterrupt.Value && rxGoodByteCounterInterruptEnable.Value) ||
            (rxByteCounterInterrupt.Value && rxByteCounterInterruptEnable.Value) ||
            (rxPacketCounterInterrupt.Value && rxPacketCounterInterruptEnable.Value);

        private IBusController Bus { get; }

        private IPeripheral BusContext => this;

        private AddressWidth address64Value;
        private PhyInterface activePhyValue;
        private uint timestampSecondTimer;
        private ulong ptpClockFrequency;
        private readonly LimitTimer timestampSubsecondTimer;
        private readonly MACAddress? externalMacAddress;

        private const ulong CounterMaxValue = UInt32.MaxValue;
        private const uint RxWatchdogDivider = 256;
        private const uint EtherTypeMinimalValue = 0x600;
        private const ulong DigitalSubsecondRollover = 0x3B9AC9FFUL;
        private const ulong BinarySubsecondRollover = 0x7FFFFFFFUL;

        private const int MaxDMAChannels = 8;
        private const int MinimumQueueSize = 128;

        public enum AddressWidth
        {
            Bits32 = 0b00,
            Bits40 = 0b01,
            Bits48 = 0b10,
            // Ob11 - Reserved
        }

        public enum PhyInterface
        {
            GMIIorMII = 0x0,
            RGMII = 0x1,
            SGMII = 0x2,
            TBI = 0x3,
            RMII = 0x4,
            RTBI = 0x5,
            SMII = 0x6,
            REVMII = 0x7,
        }

        private struct PTPInfo
        {
            public static PTPInfo? FromFrame(EthernetFrame frame, PTPVersion supportedPtpVersion)
            {
                try
                {
                    var packet = frame.UnderlyingPacket;
                    if(packet.Type == EthernetPacketType.PrecisionTimeProtocol)
                    {
                        return ExtractInfo(packet.PayloadPacket.Bytes, TransportType.Ethernet, supportedPtpVersion);
                    }
                    else if(packet.Type == EthernetPacketType.IpV4 || packet.Type == EthernetPacketType.IpV6)
                    {
                        var ipPacket = (IpPacket)packet.PayloadPacket;
                        if(!ptpIpAddresses.Contains(ipPacket.DestinationAddress))
                        {
                            return null;
                        }

                        if(ipPacket.PayloadPacket is UdpPacket udpPacket)
                        {
                            if(!ptpPorts.Contains(udpPacket.DestinationPort))
                            {
                                return null;
                            }
                            var transportType = packet.Type == EthernetPacketType.IpV4 ? TransportType.IpV4 : TransportType.IpV6;
                            return ExtractInfo(udpPacket.PayloadData, transportType, supportedPtpVersion);
                        }
                    }
                }
                catch(Exception)
                {
                    // Something went wrong during packet decoding
                }
                return null;
            }

            private static PTPInfo? ExtractInfo(byte[] ptpData, TransportType transport, PTPVersion ptpVersion)
            {
                var info = new PTPInfo();
                info.Transport = transport;
                // Documentation states that you can either process PTPv1 packets or PTPv2 packets, but never both
                if((ptpData[1] & 0x0F) - 1 != (int)ptpVersion)
                {
                    return null; // PTP version of the packet doesn't match what controller supports
                }
                info.Version = ptpVersion;

                switch(ptpData[0] & 0x0F)
                {
                case 0x0:
                    info.MessageType = PTPMessageType.Sync;
                    break;
                case 0x1:
                    info.MessageType = PTPMessageType.DelayRequest;
                    break;
                case 0x2:
                    info.MessageType = PTPMessageType.PdelayRequest;
                    break;
                case 0x3:
                    info.MessageType = PTPMessageType.PdelayResponse;
                    break;
                case 0x8:
                    info.MessageType = PTPMessageType.FollowUp;
                    break;
                case 0x9:
                    info.MessageType = PTPMessageType.DelayResponse;
                    break;
                case 0xA:
                    info.MessageType = PTPMessageType.PdelayResponseFollowUp;
                    break;
                case 0xB:
                    info.MessageType = PTPMessageType.Announce;
                    break;
                case 0xC:
                    info.MessageType = PTPMessageType.Signaling;
                    break;
                case 0xD:
                    info.MessageType = PTPMessageType.Management;
                    break;
                default:
                    return null; // Invalid message type
                }

                return info;
            }

            public enum TransportType
            {
                Ethernet,
                IpV4,
                IpV6,
            }

            private static readonly HashSet<IPAddress> ptpIpAddresses = new HashSet<IPAddress>()
            {
                IPAddress.Parse("224.0.0.107"),
                IPAddress.Parse("224.0.1.129"),
                IPAddress.Parse("224.0.1.130"),
                IPAddress.Parse("224.0.1.131"),
                IPAddress.Parse("224.0.1.132"),
                // Expanded `FF0x::181`
                IPAddress.Parse("FF00::181"),
                IPAddress.Parse("FF01::181"),
                IPAddress.Parse("FF02::181"),
                IPAddress.Parse("FF03::181"),
                IPAddress.Parse("FF04::181"),
                IPAddress.Parse("FF05::181"),
                IPAddress.Parse("FF06::181"),
                IPAddress.Parse("FF07::181"),
                IPAddress.Parse("FF08::181"),
                IPAddress.Parse("FF09::181"),
                IPAddress.Parse("FF0A::181"),
                IPAddress.Parse("FF0B::181"),
                IPAddress.Parse("FF0C::181"),
                IPAddress.Parse("FF0D::181"),
                IPAddress.Parse("FF0E::181"),
                IPAddress.Parse("FF0F::181"),
                IPAddress.Parse("FF02::6B"),
            };

            private static readonly HashSet<ushort> ptpPorts = new HashSet<ushort>(){ 319, 320 };

            public PTPMessageType MessageType { get; private set; }

            public TransportType Transport { get; private set; }

            public PTPVersion Version { get; private set; }
        }
    }
}

namespace Antmicro.Renode.Peripherals.Network
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using Antmicro.Renode.Core;
    using Antmicro.Renode.Core.Structure;
    using Antmicro.Renode.Core.Structure.Registers;
    using Antmicro.Renode.Exceptions;
    using Antmicro.Renode.Logging;
    using Antmicro.Renode.Network;
    using Antmicro.Renode.Peripherals.Timers;
    using Antmicro.Renode.Time;
    using Antmicro.Renode.Utilities;
    using PacketDotNet;
    using IPProtocolType = PacketDotNet.IPProtocolType;

    public partial class NXP_ENET_QOS
    {
        protected readonly DMAChannel[] dmaChannels;

        public enum BusWidth
        {
            Bits32  = 4,
            Bits64  = 8,
            Bits128 = 16,
        }

        protected class DMAChannel
        {
            public DMAChannel(NXP_ENET_QOS parent, int channelNumber, ulong systemClockFrequency, bool hasInterrupts)
            {
                this.parent = parent;
                this.channelNumber = channelNumber;
                this.hasInterrupts = hasInterrupts;

                if(hasInterrupts)
                {
                    TxIRQ = new GPIO();
                    RxIRQ = new GPIO();
                }

                incomingFrames = new Queue<EthernetFrame>();
                rxWatchdog = new LimitTimer(parent.machine.ClockSource, systemClockFrequency, parent, $"DMA Channel {channelNumber}: rx-watchdog", enabled: false, workMode: WorkMode.OneShot, eventEnabled: true, autoUpdate: true, divider: RxWatchdogDivider);
                rxWatchdog.LimitReached += delegate
                {
                    this.Log(LogLevel.Noisy, "Receive: Watchdog reached limit.");
                    rxInterrupt.Value = true;
                    parent.UpdateInterrupts();
                };
            }

            public void Reset()
            {
                rxFinishedRing = true;
                txFinishedRing = true;
                txState = DMAState.Stopped;
                rxState = DMAState.Stopped;
                rxOffset = 0;
                latestTxContext = null;
                rxWatchdog.Reset();
                incomingFrames.Clear();
                rxQueueLength = 0;
                frameAssembler = null;
            }

            public void DefineChannelRegisters(ref Dictionary<long, DoubleWordRegister> map)
            {
                // This is an offset from the start of the DMA block, not from the start of the entire peripheral
                // as the DMA block may be placed in a separate connection region.
                var offset = 0x100 + 0x80 * channelNumber;
                map = map.Concat(new Dictionary<long, DoubleWordRegister>()
                {
                    {(long)RegistersDMAChannel.Control + offset, new DoubleWordRegister(parent)
                        .WithValueField(0, 14, out maximumSegmentSize, name: "DMACCR.MSS (Maximum Segment Size)")
                        .WithReservedBits(14, 2)
                        .WithFlag(16, out programmableBurstLengthTimes8, name: "DMACCR.PBLX8 (8xPBL mode)")
                        .WithReservedBits(17, 1)
                        .WithValueField(18, 3, out descriptorSkipLength, name: "DMACCR.DSL (Descriptor Skip Length)")
                        .WithReservedBits(21, 11)
                    },
                    {(long)RegistersDMAChannel.TransmitControl + offset, new DoubleWordRegister(parent)
                        .WithFlag(0, out startTx, changeCallback: (_, __) =>
                        {
                            if(startTx.Value)
                            {
                                txFinishedRing = txDescriptorRingCurrent.Value == txDescriptorRingTail.Value;
                                StartTx();
                            }
                        },
                        name: "DMACTxCR.ST (Start or Stop Transmission Command)")
                        .WithReservedBits(1, 3)
                        .WithFlag(4, out operateOnSecondPacket, name: "DMACTxCR.OSF (Operate on Second Packet)")
                        .WithReservedBits(5, 7)
                        .WithFlag(12, out tcpSegmentationEnable, name: "DMACTxCR.TSE (TCP Segmentation Enabled)")
                        .WithReservedBits(13, 3)
                        .WithValueField(16, 6, out txProgrammableBurstLength, name: "DMACTxCR.TXPBL (Transmit Programmable Burst Length)")
                        .WithReservedBits(22, 10)
                    },
                    {(long)RegistersDMAChannel.ReceiveControl + offset, new DoubleWordRegister(parent)
                        .WithFlag(0, out startRx, changeCallback: (_, __) =>
                        {
                            if(startRx.Value)
                            {
                                rxFinishedRing = rxDescriptorRingCurrent.Value == rxDescriptorRingTail.Value;
                                StartRx();
                            }
                        },
                        name: "DMACRxCR.SR (Start or Stop Receive Command)")
                        .WithValueField(1, 14, out rxBufferSize, writeCallback: (_, __) =>
                        {
                            if(rxBufferSize.Value % 4 != 0)
                            {
                                this.Log(LogLevel.Warning, "Receive buffer size must be a multiple of 4. Ignoring LSBs, but this behavior is undefined.");
                                rxBufferSize.Value &= ~0x3UL;
                            }
                        },
                        name: "DMACRxCR.RBSZ (Receive Buffer size)")
                        .WithReservedBits(15, 1)
                        .WithValueField(16, 6, out rxProgrammableBurstLength, name: "DMACRxCR.RXPBL (RXPBL)")
                        .WithReservedBits(22, 9)
                        .WithTaggedFlag("DMACRxCR.RPF (DMA Rx Channel Packet Flush)", 31)
                    },
                    {(long)RegistersDMAChannel.TxDescriptorListAddress  + offset, new DoubleWordRegister(parent)
                        .WithValueField(0, 32, out txDescriptorRingStart, writeCallback: (_, __) =>
                        {
                            txDescriptorRingStart.Value &= DMAAddressMask;
                            txDescriptorRingCurrent.Value = txDescriptorRingStart.Value;
                        },
                        name: "DMACTxDLAR.TDESLA (Start of Transmit List)")
                    },
                    {(long)RegistersDMAChannel.RxDescriptorListAddress + offset, new DoubleWordRegister(parent)
                        .WithValueField(0, 32, out rxDescriptorRingStart, writeCallback: (_, __) =>
                        {
                            rxDescriptorRingStart.Value &= DMAAddressMask;
                            rxDescriptorRingCurrent.Value = rxDescriptorRingStart.Value;
                        },
                        name: "DMACRxDLAR.RDESLA (Start of Receive List)")
                    },
                    {(long)RegistersDMAChannel.TxDescriptorTailPointer + offset, new DoubleWordRegister(parent)
                        .WithValueField(0, 32, out txDescriptorRingTail, writeCallback: (previousValue, _) =>
                        {
                            txDescriptorRingTail.Value &= DMAAddressMask;
                            var clearTxFinishedRing = txDescriptorRingTail.Value != txDescriptorRingCurrent.Value;
                            if((txState & DMAState.Suspended) != 0 || clearTxFinishedRing)
                            {
                                txFinishedRing &= !clearTxFinishedRing;
                                StartTx();
                            }
                            this.Log(LogLevel.Debug, "Transmit Tail register (DMACTxDTPR.TDT) set to: 0x{0:X}", txDescriptorRingTail.Value);
                        }, name: "DMACTxDTPR.TDT (Transmit Descriptor Tail Pointer)")
                    },
                    {(long)RegistersDMAChannel.RxDescriptorTailPointer + offset, new DoubleWordRegister(parent)
                        .WithValueField(0, 32, out rxDescriptorRingTail, writeCallback: (previousValue, _) =>
                        {
                            rxDescriptorRingTail.Value &= DMAAddressMask;
                            var clearRxFinishedRing = rxDescriptorRingTail.Value != rxDescriptorRingCurrent.Value;
                            if((rxState & DMAState.Suspended) != 0 || clearRxFinishedRing)
                            {
                                rxFinishedRing &= !clearRxFinishedRing;
                                StartRx();
                            }
                            this.Log(LogLevel.Debug, "Receive Tail register (DMACRxDTPR.RDT) set to: 0x{0:X}", rxDescriptorRingTail.Value);
                        }, name: "DMACRxDTPR.RDT (Receive Descriptor Tail Pointer)")
                    },
                    {(long)RegistersDMAChannel.TxDescriptorRingLength + offset, new DoubleWordRegister(parent)
                        .WithValueField(0, 10, out txDescriptorRingLength, name: "DMACTxRLR.TDRL (Transmit Descriptor Ring Length)")
                        .WithReservedBits(10, 22)
                    },
                    {(long)RegistersDMAChannel.RxDescriptorRingLength + offset, new DoubleWordRegister(parent)
                        .WithValueField(0, 10, out rxDescriptorRingLength, name: "DMACRxRLR.RDRL (Receive Descriptor Ring Length)")
                        .WithReservedBits(10, 6)
                        .WithValueField(16, 8, out alternateRxBufferSize, name: "DMACRxRLR.ARBS (Alternate Receive Buffer Size)")
                        .WithReservedBits(24, 8)
                    },
                    {(long)RegistersDMAChannel.InterruptEnable + offset, new DoubleWordRegister(parent)
                        .WithFlag(0, out txInterruptEnable, name: "DMACIER.TIE (Transmit Interrupt Enable)")
                        .WithFlag(1, out txProcessStoppedEnable, name: "DMACIER.TXSE (Transmit Stopped Enable)")
                        .WithFlag(2, out txBufferUnavailableEnable, name: "DMACIER.TBUE (Transmit Buffer Unavailable Enable)")
                        .WithReservedBits(3, 3)
                        .WithFlag(6, out rxInterruptEnable, name: "DMACIER.RIE (Receive Interrupt Enable)")
                        .WithFlag(7, out rxBufferUnavailableEnable, name: "DMACIER.RBUE (Receive Buffer Unavailable Enable)")
                        .WithFlag(8, out rxProcessStoppedEnable, name: "DMACIER.RSE (Receive Stopped Enable)")
                        .WithFlag(9, out rxWatchdogTimeoutEnable, name: "DMACIER.RWTE (Receive Watchdog Timeout Enable)")
                        .WithFlag(10, out earlyTxInterruptEnable, name: "DMACIER.ETIE (Early Transmit Interrupt Enable)")
                        .WithFlag(11, out earlyRxInterruptEnable, name: "DMACIER.ERIE (Early Receive Interrupt Enable)")
                        .WithFlag(12, out fatalBusErrorEnable, name: "DMACIER.FBEE (Fatal Bus Error Enable)")
                        .WithFlag(13, out contextDescriptorErrorEnable, name: "DMACIER.CDEE (Context Descriptor Error Enable)")
                        .WithFlag(14, out abnormalInterruptSummaryEnable, name: "DMACIER.AIE (Abnormal Interrupt Summary Enable)")
                        .WithFlag(15, out normalInterruptSummaryEnable, name: "DMACIER.NIE (Normal Interrupt Summary Enable)")
                        .WithReservedBits(16, 16)
                        .WithChangeCallback((_, __) => parent.UpdateInterrupts())
                    },
                    {(long)RegistersDMAChannel.RxInterruptWatchdogTimer + offset, new DoubleWordRegister(parent)
                        .WithValueField(0, 8, out rxWatchdogCounter, changeCallback: (_, __) =>
                        {
                            rxWatchdog.Limit = rxWatchdogCounter.Value;
                        },
                        name: "DMACRxIWTR.RWT (Receive Interrupt Watchdog Timer Count)")
                        .WithReservedBits(8, 8)
                        .WithValueField(16, 2, out rxWatchdogCounterUnit, changeCallback: (_, __) =>
                        {
                            rxWatchdog.Divider = RxWatchdogDivider << (byte)rxWatchdogCounterUnit.Value;
                        },
                        name: "DMACRxIWTR.RWTU (Receive Interrupt Watchdog Timer Count Units)")
                        .WithReservedBits(18, 14)
                    },
                    {(long)RegistersDMAChannel.CurrentApplicationTransmitDescriptor + offset, new DoubleWordRegister(parent)
                        .WithValueField(0, 32, out txDescriptorRingCurrent, FieldMode.Read, name: "DMACCATxDR.CURTDESAPTR (Application Transmit Descriptor Address Pointer)")
                    },
                    {(long)RegistersDMAChannel.CurrentApplicationReceiveDescriptor + offset, new DoubleWordRegister(parent)
                        .WithValueField(0, 32, out rxDescriptorRingCurrent, FieldMode.Read, name: "DMACCARxDR.CURRDESAPTR (Application Receive Descriptor Address Pointer)")
                    },
                    {(long)RegistersDMAChannel.CurrentApplicationTransmitBuffer + offset, new DoubleWordRegister(parent)
                        .WithValueField(0, 32, out txCurrentBuffer, FieldMode.Read, name: "DMACCATxBR.CURTBUFAPTR (Application Transmit Buffer Address Pointer)")
                    },
                    {(long)RegistersDMAChannel.CurrentApplicationReceiveBuffer + offset, new DoubleWordRegister(parent)
                        .WithValueField(0, 32, out rxCurrentBuffer, FieldMode.Read, name: "DMACCARxBR.CURRBUFAPTR (Application Receive Buffer Address Pointer)")
                    },
                    {(long)RegistersDMAChannel.Status + offset, new DoubleWordRegister(parent)
                        .WithFlag(0, out txInterrupt, FieldMode.Read | FieldMode.WriteOneToClear, name: "DMACSR.TI (Transmit Interrupt)")
                        .WithFlag(1, out txProcessStopped, FieldMode.Read | FieldMode.WriteOneToClear, name: "DMACSR.TPS (Transmit Process Stopped)")
                        .WithFlag(2, out txBufferUnavailable, FieldMode.Read | FieldMode.WriteOneToClear, name: "DMACSR.TBU (Transmit Buffer Unavailable)")
                        .WithReservedBits(3, 3)
                        .WithFlag(6, out rxInterrupt, FieldMode.Read | FieldMode.WriteOneToClear, name: "DMACSR.RI (Receive Interrupt)")
                        .WithFlag(7, out rxBufferUnavailable, FieldMode.Read | FieldMode.WriteOneToClear, name: "DMACSR.RBU (Receive Buffer Unavailable)")
                        .WithFlag(8, out rxProcessStopped, FieldMode.Read | FieldMode.WriteOneToClear, name: "DMACSR.RPS (Receive Process Stopped)")
                        .WithFlag(9, out rxWatchdogTimeout, FieldMode.Read | FieldMode.WriteOneToClear, name: "DMACSR.RWT (Receive Watchdog Timeout)")
                        .WithFlag(10, out earlyTxInterrupt, FieldMode.Read | FieldMode.WriteOneToClear, name: "DMACSR.ET (Early Transmit Interrupt)")
                        .WithFlag(11, out earlyRxInterrupt, FieldMode.Read | FieldMode.WriteOneToClear, name: "DMACSR.ER (Early Receive Interrupt)")
                        .WithFlag(12, out fatalBusError, FieldMode.Read | FieldMode.WriteOneToClear, name: "DMACSR.FBE (Fatal Bus Error)")
                        .WithFlag(13, out contextDescriptorError, FieldMode.Read | FieldMode.WriteOneToClear, name: "DMACSR.CDE (Context Descriptor Error)")
                        .WithFlag(14, out abnormalInterruptSummary, FieldMode.Read | FieldMode.WriteOneToClear, name: "DMACSR.AIS (Abnormal Interrupt Summary)")
                        .WithFlag(15, out normalInterruptSummary, FieldMode.Read | FieldMode.WriteOneToClear, name: "DMACSR.NIS (Normal Interrupt Summary)")
                        .WithTag("DMACSR.TEB (Tx DMA Error Bits)", 16, 3)
                        .WithTag("DMACSR.REB (Rx DMA Error Bits)", 19, 3)
                        .WithReservedBits(22, 10)
                        .WithChangeCallback((_, __) => parent.UpdateInterrupts())
                    },
                    {(long)RegistersDMAChannel.MissedFrameCount + offset, new DoubleWordRegister(parent)
                        .WithTag("DMACMFCR.MFC (Dropped Packet Counters)", 0, 11)
                        .WithReservedBits(11, 4)
                        .WithTaggedFlag("DMACMFCR.MFCO (Overflow status of the MFC Counter)", 15)
                        .WithReservedBits(16, 16)
                    }
                }).ToDictionary(x => x.Key, x => x.Value);
            }

            public void ReceiveFrame(EthernetFrame frame)
            {
                if(rxQueueLength + frame.Length > parent.RxQueueSize)
                {
                    parent.IncrementPacketCounter(parent.rxFifoPacketCounter, parent.rxFifoPacketCounterInterrupt);
                    this.Log(LogLevel.Debug, "Receive: Dropping overflow frame {0}", frame);
                    parent.UpdateInterrupts();
                    return;
                }

                this.Log(LogLevel.Debug, "Receive: Incoming frame {0}", frame);
                incomingFrames.Enqueue(frame);
                rxQueueLength += frame.Bytes.Length;
                StartRx();
            }

            public bool UpdateInterrupts()
            {
                // Depending on the DMA interrupt mode transmit/receive completed condition may not
                // contribute to the common interrupt. Those masks are used to block those conditions from
                // influencing the common interrupt state.
                var txMask = true;
                var rxMask = true;

                if(hasInterrupts)
                {
                    switch(parent.dmaInterruptMode.Value)
                    {
                    case DMAChannelInterruptMode.Pulse:
                        if(txInterrupt.Value)
                        {
                            TxIRQ.Blink();
                            this.Log(LogLevel.Debug, "Blinking TxIRQ");
                        }
                        if(rxInterrupt.Value)
                        {
                            RxIRQ.Blink();
                            this.Log(LogLevel.Debug, "Blinking RxIRQ");
                        }
                        break;
                    case DMAChannelInterruptMode.Level:
                    case DMAChannelInterruptMode.LevelAndReassert:
                    {
                        var txState = txInterrupt.Value && txInterruptEnable.Value && normalInterruptSummaryEnable.Value;
                        var rxState = rxInterrupt.Value && rxInterruptEnable.Value && normalInterruptSummaryEnable.Value;
                        TxIRQ.Set(txState);
                        RxIRQ.Set(rxState);
                        this.Log(LogLevel.Debug, "TxIRQ: {0}, RxIRQ: {1}", txState ? "setting" : "unsetting", rxState ? "setting" : "unsetting");
                        // In both Level and LevelAndReassert transmit/receive completed conditions are only used for channel specific interrupts
                        // and don't contribute to the common interrupt. Mask their influence
                        txMask = false;
                        rxMask = false;
                        break;
                    }
                    default:
                        this.Log(LogLevel.Warning, "Invalid interrupt mode value {0}", parent.dmaInterruptMode.Value);
                        break;
                    }
                }

                normalInterruptSummary.Value |= GetNormalInterruptSummary();
                abnormalInterruptSummary.Value |= (txProcessStopped.Value && txProcessStoppedEnable.Value) ||
                                                  (rxBufferUnavailable.Value && rxBufferUnavailableEnable.Value) ||
                                                  (rxProcessStopped.Value && rxProcessStoppedEnable.Value) ||
                                                  (earlyTxInterrupt.Value && earlyTxInterruptEnable.Value) ||
                                                  (fatalBusError.Value && fatalBusErrorEnable.Value) ||
                                                  (contextDescriptorError.Value && contextDescriptorErrorEnable.Value);

                return (rxWatchdogTimeout.Value && rxWatchdogTimeoutEnable.Value) ||
                       (abnormalInterruptSummary.Value && abnormalInterruptSummaryEnable.Value) ||
                       (GetNormalInterruptSummary(txMask, rxMask) && normalInterruptSummaryEnable.Value);
            }

            public DMATxProcessState DmaTxState => (txState == DMAState.Stopped) ? DMATxProcessState.Stopped : DMATxProcessState.Suspended;

            public DMARxProcessState DmaRxState => (rxState == DMAState.Stopped) ? DMARxProcessState.Stopped :
                                            ((incomingFrames.Count == 0) ? DMARxProcessState.WaitingForPacket : DMARxProcessState.Suspended);

            public bool Interrupts =>
                txInterrupt.Value ||
                txProcessStopped.Value ||
                txBufferUnavailable.Value ||
                rxInterrupt.Value ||
                rxBufferUnavailable.Value ||
                rxProcessStopped.Value ||
                rxWatchdogTimeout.Value ||
                earlyTxInterrupt.Value ||
                earlyRxInterrupt.Value ||
                fatalBusError.Value ||
                abnormalInterruptSummary.Value ||
                normalInterruptSummary.Value;

            public GPIO TxIRQ { get; }

            public GPIO RxIRQ { get; }

            private TxDescriptor GetTxDescriptor()
            {
                var descriptor = new TxDescriptor(parent.Bus, txDescriptorRingCurrent.Value, parent.BusContext);
                descriptor.Fetch();
                return descriptor;
            }

            private RxDescriptor GetRxDescriptor()
            {
                var descriptor = new RxDescriptor(parent.Bus, rxDescriptorRingCurrent.Value, parent.BusContext);
                descriptor.Fetch();
                return descriptor;
            }

            private void IncreaseTxDescriptorPointer()
            {
                IncreaseDescriptorPointer(txDescriptorRingCurrent, txDescriptorRingStart, txDescriptorRingLength, "TX");
                txFinishedRing = txDescriptorRingCurrent.Value == txDescriptorRingTail.Value;
            }

            private void IncreaseRxDescriptorPointer()
            {
                IncreaseDescriptorPointer(rxDescriptorRingCurrent, rxDescriptorRingStart, rxDescriptorRingLength, "RX");
                rxFinishedRing = rxDescriptorRingCurrent.Value == rxDescriptorRingTail.Value;
            }

            private void IncreaseDescriptorPointer(IValueRegisterField current, IValueRegisterField start, IValueRegisterField length, string name)
            {
                var size = descriptorSkipLength.Value * (ulong)parent.DMABusWidth + Descriptor.Size;
                var offset = current.Value - start.Value;
                offset += size;
                // The docs state that: "If you want to have 10 descriptors, program it to a value of 0x9" - so it always should be +1 descriptor than obtained from the register
                offset %= (length.Value + 1) * size;
                this.Log(LogLevel.Noisy, "{0} Descriptor pointer was 0x{1:X}, now is 0x{2:X}, size 0x{3:X}, ring length 0x{4:x}", name, current.Value, start.Value + offset, size, length.Value);
                current.Value = start.Value + offset;
            }

            private void TriggerRxWatchdog()
            {
                rxWatchdog.Value = rxWatchdogCounter.Value;
                rxWatchdog.Enabled = rxWatchdogCounter.Value != 0 || rxWatchdogCounterUnit.Value != 0;
            }

            private void StartRx()
            {
                if(!parent.rxEnable.Value)
                {
                    rxState = DMAState.Stopped;
                    this.Log(LogLevel.Noisy, "Receive: Rx DMA is not enabled.");
                    return;
                }
                if(!startRx.Value)
                {
                    rxState = DMAState.Stopped;
                    this.Log(LogLevel.Noisy, "Receive: Rx DMA is not started.");
                    return;
                }
                if(rxState == DMAState.Stopped)
                {
                    rxState = DMAState.Running;
                    rxDescriptorRingCurrent.Value = rxDescriptorRingStart.Value;
                    this.Log(LogLevel.Debug, "Receive: Starting DMA at 0x{0:X}.", rxDescriptorRingCurrent.Value);
                }
                else
                {
                    this.Log(LogLevel.Debug, "Receive: Resuming DMA at 0x{0:X}.", rxDescriptorRingCurrent.Value);
                }

                if(incomingFrames.Count == 0)
                {
                    this.Log(LogLevel.Noisy, "Receive: No frames to process.");
                    rxState |= DMAState.Suspended;
                    return;
                }
                var frame = incomingFrames.Peek();
                var bytes = frame.Bytes;
                var isFirst = true;
                PTPInfo? ptpInfo = null;
                var insertTimestamp = false;
                while(!rxFinishedRing && parent.rxEnable.Value && startRx.Value)
                {
                    var descriptor = GetRxDescriptor();

                    if(!descriptor.IsOwnedByDMA.Value)
                    {
                        this.Log(LogLevel.Debug, "Receive: Loaded descriptor is not owned by DMA.");
                        rxBufferUnavailable.Value = true;
                        rxState |= DMAState.Suspended;
                        break;
                    }
                    rxState &= ~DMAState.Suspended;
                    var structure = descriptor.GetNormalReadDescriptor();
#if DEBUG
                    this.Log(LogLevel.Noisy, "Receive: Loaded {0} from 0x{1:X}.", structure, descriptor.Address);
#endif

                    var bufferAddress = 0UL;
                    var bufferSize = 0UL;
                    var invalidDescriptor = structure.Buffer1Address == UInt32.MaxValue || structure.Buffer2Address == UInt32.MaxValue;
                    if(!invalidDescriptor && structure.Buffer1Address != 0 && structure.Buffer1AddressValid)
                    {
                        bufferAddress = structure.Buffer1Address;
                        bufferSize = RxBuffer1Size;
                    }
                    else if(!invalidDescriptor && structure.Buffer2Address != 0 && structure.Buffer2AddressValid)
                    {
                        bufferAddress = structure.Buffer2Address;
                        bufferSize = RxBuffer2Size;
                    }
                    else
                    {
                        contextDescriptorError.Value |= invalidDescriptor;
                        this.Log(LogLevel.Debug, "Receive: Loaded descriptor doesn't provide a valid buffer.");
                        structure.OwnerField = DescriptorOwner.Application;
#if DEBUG
                        this.Log(LogLevel.Noisy, "Receive: Writing {0} to 0x{1:X}.", structure, descriptor.Address);
#endif
                        descriptor.SetDescriptor(structure);
                        descriptor.Write();
                        IncreaseRxDescriptorPointer();
                        continue;
                    }
                    rxCurrentBuffer.Value = bufferAddress;

                    if(isFirst)
                    {
                        earlyRxInterrupt.Value = true;
                        parent.UpdateInterrupts();
                    }

                    if(rxOffset >= (ulong)bytes.Length)
                    {
                        if(insertTimestamp)
                        {
                            var contextStructure = descriptor.GetAsContextDescriptor();
                            contextStructure.ContextTypeField = true;
                            contextStructure.OwnerField = DescriptorOwner.Application;
                            parent.GetTimestamp(out var seconds, out var nanoseconds);
                            contextStructure.ReceivePacketTimestamp = (seconds << 32) | nanoseconds;
#if DEBUG
                            this.Log(LogLevel.Noisy, "Receive: Writing {0} to 0x{1:X}.", contextStructure, descriptor.Address);
#endif
                            descriptor.SetDescriptor(contextStructure);
                            descriptor.Write();
                            IncreaseRxDescriptorPointer();
                        }
                        ptpInfo = null;
                        insertTimestamp = false;

                        rxOffset = 0;
                        incomingFrames.Dequeue();
                        rxQueueLength -= bytes.Length;

                        if(incomingFrames.Count == 0)
                        {
                            this.Log(LogLevel.Noisy, "Receive: Finished handling frame, no more frames to process.");
                            break;
                        }
                        this.Log(LogLevel.Noisy, "Receive: Finished handling frame, processing next frame.");
                        frame = incomingFrames.Peek();
                        isFirst = true;
                        bytes = frame.Bytes;
                        continue;
                    }

                    var bytesWritten = Math.Min((ulong)bytes.Length - rxOffset, bufferSize);
                    parent.Bus.WriteBytes(bytes, bufferAddress, (int)rxOffset, (long)bytesWritten, true, parent.BusContext);
                    this.Log(LogLevel.Noisy, "Receive: Writing frame[0x{0:X}, 0x{1:X}) at 0x{2:X}.", rxOffset, rxOffset + bytesWritten, bufferAddress);
                    rxOffset += bytesWritten;

                    var writeBackStructure = descriptor.GetAsNormalWriteBackDescriptor();
                    writeBackStructure.OwnerField = DescriptorOwner.Application;
                    writeBackStructure.FirstDescriptor = isFirst;
                    writeBackStructure.LastDescriptor = rxOffset == (ulong)bytes.Length;
                    writeBackStructure.ContextTypeField = false; ;
                    writeBackStructure.ReceiveStatusSegment0Valid = true;
                    writeBackStructure.ReceiveStatusSegment1Valid = true;
                    writeBackStructure.ReceiveStatusSegment2Valid = true;
                    isFirst = false;

                    writeBackStructure.PacketLength = (uint)bytes.Length;
                    writeBackStructure.OuterVlanTag = 0x0;
                    writeBackStructure.InnerVlanTag = 0x0;
                    writeBackStructure.OamSubtypeCodeOrMACControlPacketOpcode = (uint)frame.UnderlyingPacket.Type;
                    writeBackStructure.Ipv4HeaderPresent = frame.UnderlyingPacket.Type == EthernetPacketType.IpV4;
                    writeBackStructure.Ipv6HeaderPresent = frame.UnderlyingPacket.Type == EthernetPacketType.IpV6;

                    // Validate IP header checksum if checksum offload is enabled
                    writeBackStructure.IpHeaderError = false;
                    writeBackStructure.IpChecksumBypassed = !parent.checksumOffloadEnable.Value;
                    if(parent.checksumOffloadEnable.Value && writeBackStructure.Ipv4HeaderPresent)
                    {
                        var ipv4Packet = (IPv4Packet)frame.UnderlyingPacket.PayloadPacket;
                        writeBackStructure.IpHeaderError = !ipv4Packet.ValidChecksum;
                    }

                    if(writeBackStructure.Ipv4HeaderPresent || writeBackStructure.Ipv6HeaderPresent)
                    {
                        switch(((IpPacket)frame.UnderlyingPacket.PayloadPacket).NextHeader)
                        {
                        case IPProtocolType.UDP:
                            writeBackStructure.PayloadType = PayloadType.UDP;
                            break;
                        case IPProtocolType.TCP:
                            writeBackStructure.PayloadType = PayloadType.TCP;
                            break;
                        case IPProtocolType.ICMP:
                        case IPProtocolType.ICMPV6:
                            writeBackStructure.PayloadType = PayloadType.ICMP;
                            break;
                        case IPProtocolType.IGMP:
                            if(!writeBackStructure.Ipv4HeaderPresent)
                            {
                                goto default;
                            }
                            writeBackStructure.PayloadType = PayloadType.IGMPIPV4;
                            break;
                        default:
                            writeBackStructure.PayloadType = PayloadType.Unknown;
                            break;
                        }
                    }

                    // NOTE: VLAN tagging is not supported by PacketDotNet, the `Type` may contain a VLAN tag
                    switch(frame.UnderlyingPacket.Type)
                    {
                    case EthernetPacketType.Arp:
                        writeBackStructure.LengthTypeField = PacketKind.ARPRequest;
                        break;
                    case EthernetPacketType.MacControl:
                        writeBackStructure.LengthTypeField = PacketKind.MACControlPacket;
                        break;
                    case EthernetPacketType.VLanTaggedFrame:
                        writeBackStructure.LengthTypeField = PacketKind.TypePacketWithVLANTag;
                        break;
                    case EthernetPacketType.ProviderBridging:
                        writeBackStructure.LengthTypeField = PacketKind.TypePacketWithDoubleVLANTag;
                        break;
                    case EthernetPacketType.ConnectivityFaultManagementOrOperationsAdministrationManagement:
                        writeBackStructure.LengthTypeField = PacketKind.OAMPacket;
                        break;
                    default:
                        writeBackStructure.LengthTypeField = (uint)frame.UnderlyingPacket.Type < EtherTypeMinimalValue ? PacketKind.LengthPacket : PacketKind.TypePacket;
                        break;
                    }

                    if(writeBackStructure.LastDescriptor)
                    {
                        if(parent.enableTimestamp.Value)
                        {
                            var parsePtp = false;
                            switch(frame.UnderlyingPacket.Type)
                            {
                            case EthernetPacketType.PrecisionTimeProtocol:
                                parsePtp = parent.processPtpOverEthernet.Value;
                                break;
                            case EthernetPacketType.IpV4:
                                parsePtp = parent.processPtpOverIpv4.Value;
                                break;
                            case EthernetPacketType.IpV6:
                                parsePtp = parent.processPtpOverIpv6.Value;
                                break;
                            }
                            ptpInfo = parsePtp ? PTPInfo.FromFrame(frame, parent.usedPtpVersion.Value) : null;
                            insertTimestamp = parent.enableTimestamp.Value && (ptpInfo != null || parent.enableTimestampForAll.Value);
                        }
                        else
                        {
                            ptpInfo = null;
                            insertTimestamp = false;
                        }
                    }

                    writeBackStructure.TimestampAvailable = insertTimestamp;
                    writeBackStructure.PtpMessageType = ptpInfo?.MessageType ?? PTPMessageType.NoPTPMessageReceived;
                    writeBackStructure.PtpPacketType = (ptpInfo?.Transport ?? PTPInfo.TransportType.IpV4) == PTPInfo.TransportType.Ethernet;
                    writeBackStructure.PtpVersion = ptpInfo?.Version ?? PTPVersion.IEEE1588version1;
                    writeBackStructure.TimestampDropped = false;
                    writeBackStructure.DribbleBitError = false;
                    writeBackStructure.ReceiveError = false;
                    writeBackStructure.OverflowError = false;
                    writeBackStructure.ReceiveWatchdogTimeout = false;
                    writeBackStructure.GiantPacket = false;
                    writeBackStructure.CrcError = parent.crcCheckDisable.Value ? false : !EthernetFrame.CheckCRC(bytes);

                    // Validate payload checksum if checksum offload is enabled
                    writeBackStructure.IpPayloadError = false;
                    if(parent.checksumOffloadEnable.Value && (writeBackStructure.Ipv4HeaderPresent || writeBackStructure.Ipv6HeaderPresent))
                    {
                        var ipPacket = (IpPacket)frame.UnderlyingPacket.PayloadPacket;
                        if(ipPacket.PayloadPacket is TcpPacket tcpPacket)
                        {
                            writeBackStructure.IpPayloadError = !tcpPacket.ValidChecksum;
                        }
                        else if(ipPacket.PayloadPacket is UdpPacket udpPacket)
                        {
                            // UDP checksum is optional in IPv4, mandatory in IPv6
                            if(udpPacket.Checksum != 0 || writeBackStructure.Ipv6HeaderPresent)
                            {
                                writeBackStructure.IpPayloadError = !udpPacket.ValidChecksum;
                            }
                        }
                        // Note: ICMP checksum validation could be added here if PacketDotNet supports it
                    }

                    writeBackStructure.ErrorSummary = new bool[]
                    {
                        writeBackStructure.DribbleBitError,
                        writeBackStructure.ReceiveError,
                        writeBackStructure.OverflowError,
                        writeBackStructure.ReceiveWatchdogTimeout,
                        writeBackStructure.GiantPacket,
                        writeBackStructure.CrcError,
                    }.Any(x => x);
#if DEBUG
                    this.Log(LogLevel.Noisy, "Receive: Writing {0} to 0x{1:X}.", writeBackStructure, descriptor.Address);
#endif
                    descriptor.SetDescriptor(writeBackStructure);
                    descriptor.Write();
                    IncreaseRxDescriptorPointer();

                    if(!writeBackStructure.LastDescriptor)
                    {
                        continue;
                    }

                    if(structure.InterruptOnCompletion)
                    {
                        rxInterrupt.Value = true;
                        rxWatchdog.Enabled = false;
                    }
                    else
                    {
                        TriggerRxWatchdog();
                    }
                    earlyRxInterrupt.Value = false;
                    parent.UpdateRxCounters(frame, writeBackStructure);
                    this.Log(LogLevel.Noisy, "Receive: Frame fully processed.");
                }
                if(!parent.rxEnable.Value || !startRx.Value)
                {
                    rxProcessStopped.Value = true;
                    rxState = DMAState.Stopped;
                    this.Log(LogLevel.Debug, "Receive: Stopping Rx DMA at 0x{0:X}.", rxDescriptorRingCurrent.Value);
                }
                else
                {
                    if(rxFinishedRing)
                    {
                        this.Log(LogLevel.Noisy, "Receive: Descriptor ring is empty.");
                    }
                    rxBufferUnavailable.Value |= rxFinishedRing || incomingFrames.Count != 0;
                    rxState |= DMAState.Suspended;
                    this.Log(LogLevel.Debug, "Receive: Suspending Rx DMA at 0x{0:X}.", rxDescriptorRingCurrent.Value);
                }
                parent.UpdateInterrupts();
            }

            private void StartTx()
            {
                if(!parent.txEnable.Value)
                {
                    txState = DMAState.Stopped;
                    this.Log(LogLevel.Noisy, "Transmission: Tx DMA is not enabled.");
                    return;
                }
                if(!startTx.Value)
                {
                    txState = DMAState.Stopped;
                    this.Log(LogLevel.Noisy, "Transmission: Tx DMA is not started.");
                    return;
                }
                if(txState == DMAState.Stopped)
                {
                    txState |= DMAState.Running;
                    txDescriptorRingCurrent.Value = txDescriptorRingStart.Value;
                    this.Log(LogLevel.Debug, "Transmission: Starting Tx DMA at 0x{0:X}.", txDescriptorRingCurrent.Value);
                }
                else
                {
                    this.Log(LogLevel.Debug, "Transmission: Resuming Tx DMA at 0x{0:X}.", txDescriptorRingCurrent.Value);
                }

                while(!txFinishedRing && parent.txEnable.Value && startTx.Value)
                {
                    var descriptor = GetTxDescriptor();

                    if(!descriptor.IsOwnedByDMA.Value)
                    {
                        this.Log(LogLevel.Debug, "Transmission: Loaded descriptor is not owned by DMA.");
                        txProcessStopped.Value = true;
                        txBufferUnavailable.Value = true;
                        txState |= DMAState.Suspended;
                        this.Log(LogLevel.Debug, "Transmission: Suspending Tx DMA at 0x{0:X}.", txDescriptorRingCurrent.Value);
                        break;
                    }
                    txState &= ~DMAState.Suspended;
                    if(descriptor.Type.Is<TxDescriptor.NormalReadDescriptor>())
                    {
                        var structure = descriptor.GetNormalReadDescriptor();
#if DEBUG
                        this.Log(LogLevel.Noisy, "Transmission: Loaded {0} from 0x{1:X}.", structure, descriptor.Address);
#endif
                        if(frameAssembler == null && !structure.FirstDescriptor)
                        {
                            this.Log(LogLevel.Warning, "Transmission: Building frame without first descriptor.");
                            break;
                        }
                        else if(frameAssembler != null && structure.FirstDescriptor)
                        {
                            this.Log(LogLevel.Warning, "Transmission: Building new frame without clearing last frame.");
                        }

                        var buffer = structure.FetchBuffer1OrHeader(parent.Bus, parent.BusContext);
                        txCurrentBuffer.Value = structure.Buffer1OrHeaderAddress;
                        var tsoEnabled = structure.TcpSegmentationEnable && tcpSegmentationEnable.Value;

                        MACAddress? sourceAddress = null;
                        switch(structure.SourceAddressControl)
                        {
                        case DescriptorSourceAddressOperation.MACAddressRegister0Insert:
                        case DescriptorSourceAddressOperation.MACAddressRegister0Replace:
                            sourceAddress = parent.MAC0;
                            break;
                        case DescriptorSourceAddressOperation.MACAddressRegister1Insert:
                        case DescriptorSourceAddressOperation.MACAddressRegister1Replace:
                            sourceAddress = parent.MAC1;
                            break;
                        default:
                            sourceAddress = null;
                            break;
                        }

                        if(!sourceAddress.HasValue)
                        {
                            switch(parent.sourceAddressOperation.Value)
                            {
                            case RegisterSourceAddressOperation.MACAddressRegister0Insert:
                            case RegisterSourceAddressOperation.MACAddressRegister0Replace:
                                sourceAddress = parent.MAC0;
                                break;
                            case RegisterSourceAddressOperation.MACAddressRegister1Insert:
                            case RegisterSourceAddressOperation.MACAddressRegister1Replace:
                                sourceAddress = parent.MAC1;
                                break;
                            case RegisterSourceAddressOperation.MACAddressFromInputSignals:
                                if(!parent.externalMacAddress.HasValue)
                                {
                                    this.Log(LogLevel.Warning, "Attempted to use external MAC address, but no address was provied");
                                }
                                else
                                {
                                    sourceAddress = parent.externalMacAddress;
                                }
                                break;
                            default:
                                this.Log(LogLevel.Error, "Using a reserved value in ETH_MACCR.SARC register.");
                                break;
                            }
                        }

                        if(structure.FirstDescriptor)
                        {
                            if(tsoEnabled)
                            {
                                frameAssembler = new FrameAssembler(
                                    parent,
                                    buffer,
                                    (uint)maximumSegmentSize.Value,
                                    latestTxContext,
                                    true, // TSO always requires checksum offload
                                    parent.SendFrame,
                                    sourceAddress
                                );
                                buffer = structure.FetchBuffer2OrBuffer1(parent.Bus, parent.BusContext);
                                txCurrentBuffer.Value = structure.Buffer2orBuffer1Address;
                            }
                            else
                            {
                                frameAssembler = new FrameAssembler(
                                    parent,
                                    structure.CrcPadControl,
                                    structure.ChecksumControl, // TX checksum insertion is controlled by descriptor CIC field, not MACCR.IPC (RX-only)
                                    parent.SendFrame
                                );
                            }
                        }
                        if(structure.Buffer2Length != 0 && !tsoEnabled)
                        {
                            // Though it's not clearly stated in the documentation, the STM driver
                            // expects buffer2 to be valid even with TSO disabled. In this case,
                            // concatenate two buffers when assembling frame to be sent.
                            frameAssembler.PushPayload(buffer);
                            buffer = structure.FetchBuffer2OrBuffer1(parent.Bus, parent.BusContext);
                            txCurrentBuffer.Value = structure.Buffer2orBuffer1Address;
                        }
                        frameAssembler.PushPayload(buffer);
                        earlyTxInterrupt.Value = true;

                        if(!structure.LastDescriptor)
                        {
                            txState |= DMAState.ProcessingIntermediate;
                            var writeBackIntermediateStructure = new TxDescriptor.NormalWriteBackDescriptor();
                            writeBackIntermediateStructure.OwnerField = DescriptorOwner.Application;
#if DEBUG
                            this.Log(LogLevel.Noisy, "Transmission: Writing intermediate {0} to 0x{1:X}.", writeBackIntermediateStructure, descriptor.Address);
#endif
                            descriptor.SetDescriptor(writeBackIntermediateStructure);
                            descriptor.Write();
                            IncreaseTxDescriptorPointer();
                            continue;
                        }

                        frameAssembler.FinalizeAssembly();
                        frameAssembler = null;

                        if((txState & DMAState.ProcessingSecond) == 0 && operateOnSecondPacket.Value)
                        {
                            txState |= DMAState.ProcessingSecond;
                            // Even with OSP mode we do nothing more here!
                            // In OSP mode the DMA is expected to:
                            // * fetch the second frame without needing to wait for the status of first descriptor
                            // * wait for the status of the first frame before writing it back
                            // * and repeat
                            // We can immediately close the first descriptor as at this point that frame
                            // has already been transmitted, so we can write back the status and fetch the second descriptor
                            // in the next loop iteration if it is available. So for this model OSP mode is a no-op due
                            // to instant frame transmission.
                        }

                        var writeBackStructure = new TxDescriptor.NormalWriteBackDescriptor();
                        writeBackStructure.IpHeaderError = false;
                        writeBackStructure.DeferredBit = false;
                        writeBackStructure.UnderflowError = structure.Buffer1OrHeaderAddress == 0x0 || structure.HeaderOrBuffer1Length == 0x0;
                        writeBackStructure.ExcessiveDeferral = false;
                        writeBackStructure.CollisionCount = false;
                        writeBackStructure.ExcessiveCollision = false;
                        writeBackStructure.LateCollision = false;
                        writeBackStructure.NoCarrier = false;
                        writeBackStructure.LossOfCarrier = false;
                        // Payload checksum errors are reported on RX path, but TX path report is not implemented yet by this model
                        writeBackStructure.PayloadChecksumError = false;
                        writeBackStructure.PacketFlushed = false;
                        writeBackStructure.JabberTimeout = false;
                        writeBackStructure.ErrorSummary = new bool[]
                        {
                            writeBackStructure.IpHeaderError,
                            writeBackStructure.JabberTimeout,
                            writeBackStructure.PacketFlushed,
                            writeBackStructure.PayloadChecksumError,
                            writeBackStructure.LossOfCarrier,
                            writeBackStructure.NoCarrier,
                            writeBackStructure.LateCollision,
                            writeBackStructure.ExcessiveCollision,
                            writeBackStructure.ExcessiveDeferral,
                            writeBackStructure.UnderflowError,
                        }.Any(x => x);
                        writeBackStructure.TxTimestampCaptured = false;
                        writeBackStructure.OwnerField = DescriptorOwner.Application;
                        writeBackStructure.FirstDescriptor = structure.FirstDescriptor;
                        writeBackStructure.LastDescriptor = structure.LastDescriptor;

                        if(structure.TransmitTimestampEnable && parent.enableTimestamp.Value)
                        {
                            writeBackStructure.TxTimestampCaptured = true;
                            parent.GetTimestamp(out var seconds, out var nanoseconds);
                            writeBackStructure.TxPacketTimestamp = (seconds << 32) | nanoseconds;
                        }
#if DEBUG
                        this.Log(LogLevel.Noisy, "Transmission: Writing {0} to 0x{1:X}.", writeBackStructure, descriptor.Address);
#endif
                        descriptor.SetDescriptor(writeBackStructure);

                        if(structure.InterruptOnCompletion)
                        {
                            txInterrupt.Value = true;
                        }
                    }
                    else if(descriptor.Type.Is<TxDescriptor.ContextDescriptor>())
                    {
                        var structure = descriptor.GetContextDescriptor();
                        latestTxContext = structure;
#if DEBUG
                        this.Log(LogLevel.Noisy, "Transmission: Loaded {0} from 0x{1:X}.", structure, descriptor.Address);
#endif
                        if(structure.OneStepTimestampCorrectionEnable && structure.OneStepTimestampCorrectionInputOrMaximumSegmentSizeValid)
                        {
                            this.Log(LogLevel.Error, "Transmission: Timestamping is not supported. One Step Timestamp Correction failed.");
                        }
                        structure.OwnerField = DescriptorOwner.Application;
#if DEBUG
                        this.Log(LogLevel.Noisy, "Transmission: Writing {0} to 0x{1:X}.", structure, descriptor.Address);
#endif
                        descriptor.SetDescriptor(structure);
                    }
                    else
                    {
                        throw new RecoverableException("Unreachable");
                    }
                    descriptor.Write();
                    IncreaseTxDescriptorPointer();
                }

                if(txFinishedRing)
                {
                    txBufferUnavailable.Value = true;
                    txState |= DMAState.Suspended;
                    this.Log(LogLevel.Debug, "Transmission: Descriptor ring is empty.");
                }
                if(!parent.txEnable.Value || !startTx.Value)
                {
                    txState = DMAState.Stopped;
                    txProcessStopped.Value = true;
                    this.Log(LogLevel.Debug, "Transmission: Stopping Tx DMA at 0x{0:X}.", txDescriptorRingCurrent.Value);
                }
                parent.UpdateInterrupts();
            }

            private void Log(LogLevel level, string format, params object[] args)
            {
                parent.Log(level, $"DMA Channel {channelNumber}: {format}", args);
            }

            private bool GetNormalInterruptSummary(bool txMask = true, bool rxMask = true)
            {
                return (txMask && txInterrupt.Value && txInterruptEnable.Value) ||
                       (txBufferUnavailable.Value && txBufferUnavailableEnable.Value) ||
                       (rxMask && rxInterrupt.Value && rxInterruptEnable.Value) ||
                       (earlyRxInterrupt.Value && earlyRxInterruptEnable.Value);
            }

            private ulong RxBuffer1Size => alternateRxBufferSize.Value == 0 ? rxBufferSize.Value : alternateRxBufferSize.Value;

            private ulong RxBuffer2Size => rxBufferSize.Value;

            // TODO: Maybe remove those:
            private ulong ProgrammableBurstLengthMultiplier => programmableBurstLengthTimes8.Value ? 8UL : 1UL;

            private ulong TxProgrammableBurstLength => txProgrammableBurstLength.Value * ProgrammableBurstLengthMultiplier;

            private ulong RxProgrammableBurstLength => rxProgrammableBurstLength.Value * ProgrammableBurstLengthMultiplier;

            private ulong DMAAddressMask => parent.DMABusWidth switch
            {
                BusWidth.Bits32 => ~(ulong)0b11,
                BusWidth.Bits64 => ~(ulong)0b1111,
                BusWidth.Bits128 => ~(ulong)0b11111,
                _ => throw new NotImplementedException()
            };

            private IValueRegisterField maximumSegmentSize;
            private IFlagRegisterField programmableBurstLengthTimes8;
            private IValueRegisterField descriptorSkipLength;
            private IFlagRegisterField startTx;
            private IFlagRegisterField operateOnSecondPacket;
            private IFlagRegisterField tcpSegmentationEnable;
            private IValueRegisterField txProgrammableBurstLength;
            private IFlagRegisterField startRx;
            private IValueRegisterField rxBufferSize;
            private IValueRegisterField rxProgrammableBurstLength;
            private IValueRegisterField txDescriptorRingStart;
            private IValueRegisterField rxDescriptorRingStart;
            private IValueRegisterField txDescriptorRingTail;
            private IValueRegisterField rxDescriptorRingTail;
            private IValueRegisterField txDescriptorRingLength;
            private IValueRegisterField rxDescriptorRingLength;
            private IValueRegisterField alternateRxBufferSize;
            private IValueRegisterField txDescriptorRingCurrent;
            private IValueRegisterField rxDescriptorRingCurrent;
            private IValueRegisterField txCurrentBuffer;
            private IValueRegisterField rxCurrentBuffer;

            private IFlagRegisterField txInterruptEnable;
            private IFlagRegisterField txProcessStoppedEnable;
            private IFlagRegisterField txBufferUnavailableEnable;
            private IFlagRegisterField rxInterruptEnable;
            private IFlagRegisterField rxBufferUnavailableEnable;
            private IFlagRegisterField rxProcessStoppedEnable;
            private IFlagRegisterField rxWatchdogTimeoutEnable;
            private IFlagRegisterField earlyTxInterruptEnable;
            private IFlagRegisterField earlyRxInterruptEnable;
            private IFlagRegisterField fatalBusErrorEnable;
            private IFlagRegisterField contextDescriptorErrorEnable;
            private IFlagRegisterField abnormalInterruptSummaryEnable;
            private IValueRegisterField rxWatchdogCounter;
            private IValueRegisterField rxWatchdogCounterUnit;
            private IFlagRegisterField normalInterruptSummaryEnable;
            private IFlagRegisterField txInterrupt;
            private IFlagRegisterField txProcessStopped;
            private IFlagRegisterField txBufferUnavailable;
            private IFlagRegisterField rxInterrupt;
            private IFlagRegisterField rxBufferUnavailable;
            private IFlagRegisterField rxProcessStopped;
            private IFlagRegisterField rxWatchdogTimeout;
            private IFlagRegisterField earlyTxInterrupt;
            private IFlagRegisterField earlyRxInterrupt;
            private IFlagRegisterField fatalBusError;
            private IFlagRegisterField contextDescriptorError;
            private IFlagRegisterField abnormalInterruptSummary;
            private IFlagRegisterField normalInterruptSummary;

            private FrameAssembler frameAssembler;

            private bool rxFinishedRing = true;
            private bool txFinishedRing = true;
            private DMAState txState = DMAState.Stopped;
            private DMAState rxState = DMAState.Stopped;
            private ulong rxOffset;
            private TxDescriptor.ContextDescriptor? latestTxContext;
            private int rxQueueLength;

            private readonly Queue<EthernetFrame> incomingFrames;
            private readonly LimitTimer rxWatchdog;

            private readonly NXP_ENET_QOS parent;
            private readonly int channelNumber;
            private readonly bool hasInterrupts;
        }

        protected enum DMAChannelInterruptMode
        {
            Pulse            = 0b00,
            Level            = 0b01,
            LevelAndReassert = 0b10,
            Reserved         = 0b11,
        }

        private enum DMAState
        {
            Stopped = 0,
            Running = 1,
            ProcessingIntermediate = 2,
            ProcessingSecond = 4,
            Suspended = 8,
        }
    }
}

namespace Antmicro.Renode.Peripherals.Network
{
    using System;
    using Antmicro.Renode.Network;
    using Antmicro.Renode.Peripherals.Bus;
    using Antmicro.Renode.Utilities.Packets;

    public partial class NXP_ENET_QOS
    {
        private class RxDescriptor : Descriptor
        {
            public RxDescriptor(IBusController bus, ulong address, IPeripheral context = null) : base(bus, address, context)
            {
            }

            public override void Fetch()
            {
                base.Fetch();
                type = typeof(NormalReadDescriptor);
            }

            public NormalReadDescriptor GetNormalReadDescriptor()
            {
                return Packet.Decode<NormalReadDescriptor>(data);
            }

            public NormalWriteBackDescriptor GetAsNormalWriteBackDescriptor()
            {
                return Packet.Decode<NormalWriteBackDescriptor>(data);
            }

            public ContextDescriptor GetAsContextDescriptor()
            {
                return Packet.Decode<ContextDescriptor>(data);
            }

            [LeastSignificantByteFirst]
            public struct NormalReadDescriptor : IDescriptorStruct
            {
                public override string ToString()
                {
                    return PrettyString;
                }

                public string PrettyString => $@"NormalReadRxDescriptor {{
    buffer1Address: 0x{Buffer1Address:X},
    buffer2Address: 0x{Buffer2Address:X},
    buffer1AddressValid: {Buffer1AddressValid},
    buffer2AddressValid: {Buffer2AddressValid},
    interruptOnCompletion: {InterruptOnCompletion},
    owner: {Owner}
}}";

                public bool ContextType => false;

                public DescriptorOwner Owner => OwnerField;

#pragma warning disable 649
                [PacketField, Offset(doubleWords: 0, bits: 0), Width(bits: 32)] // BUF1AP
                public uint Buffer1Address;
                // 2nd double word is reserved
                [PacketField, Offset(doubleWords: 2, bits: 0), Width(bits: 32)] // BUF2AP
                public uint Buffer2Address;
                // bits 0:23 of 4th double word are reserved
                [PacketField, Offset(doubleWords: 3, bits: 24), Width(bits: 1)] // BUF1V
                public bool Buffer1AddressValid;
                [PacketField, Offset(doubleWords: 3, bits: 25), Width(bits: 1)] // BUF2V
                public bool Buffer2AddressValid;
                // bits 26:29 of 4th double word are reserved
                [PacketField, Offset(doubleWords: 3, bits: 30), Width(bits: 1)] // IOC
                public bool InterruptOnCompletion;
                [PacketField, Offset(doubleWords: 3, bits: 31), Width(bits: 1)] // OWN
                public DescriptorOwner OwnerField;
#pragma warning restore 649
            }

            [LeastSignificantByteFirst]
            public struct NormalWriteBackDescriptor : IDescriptorStruct
            {
                public override string ToString()
                {
                    return PrettyString;
                }

                public string PrettyString => $@"NormalWriteBackRxDescriptor {{
    outerVlanTag: 0x{OuterVlanTag:X},
    innerVlanTag: 0x{InnerVlanTag:X},
    payloadType: {PayloadType},
    ipHeaderError: {IpHeaderError},
    ipv4HeaderPresent: {Ipv4HeaderPresent},
    ipv6HeaderPresent: {Ipv6HeaderPresent},
    ipChecksumBypassed: {IpChecksumBypassed},
    ipPayloadError: {IpPayloadError},
    ptpMessageType: {PtpMessageType},
    ptpPacketType: {PtpPacketType},
    ptpVersion: {PtpVersion},
    timestampAvailable: {TimestampAvailable},
    timestampDropped: {TimestampDropped},
    oamSubtypeCodeOrMACControlPacketOpcode: 0x{OamSubtypeCodeOrMACControlPacketOpcode:X} or {(EtherType)OamSubtypeCodeOrMACControlPacketOpcode},
    arpReplyNotGenerated: {ArpReplyNotGenerated},
    vlanFiletrStatus: {VlanFiletrStatus},
    sourceAddressFilterFail: {SourceAddressFilterFail},
    destinaltionAddressFilterFail: {DestinaltionAddressFilterFail},
    hashFilterStatus: {HashFilterStatus},
    macAddressMatchOrHashValue: {MacAddressMatchOrHashValue},
    layer3FilterMatch: {Layer3FilterMatch},
    layer4FilterMatch: {Layer4FilterMatch},
    matchedFilterNumber: {MatchedFilterNumber},
    packetLength: {PacketLength},
    errorSummary: {ErrorSummary},
    lengthTypeField: {LengthTypeField},
    dribbleBitError: {DribbleBitError},
    receiveError: {ReceiveError},
    overflowError: {OverflowError},
    receiveWatchdogTimeout: {ReceiveWatchdogTimeout},
    giantPacket: {GiantPacket},
    crcError: {CrcError},
    receiveStatusSegment0Valid: {ReceiveStatusSegment0Valid},
    receiveStatusSegment1Valid: {ReceiveStatusSegment1Valid},
    receiveStatusSegment2Valid: {ReceiveStatusSegment2Valid},
    lastDescriptor: {LastDescriptor},
    firstDescriptor: {FirstDescriptor},
    contextType: {ContextType},
    owner: {Owner}
}}";

                public bool ContextType => ContextTypeField;

                public DescriptorOwner Owner => OwnerField;

#pragma warning disable 649
                [PacketField, Offset(doubleWords: 0, bits: 0), Width(bits: 16)] // OVT
                public uint OuterVlanTag;
                [PacketField, Offset(doubleWords: 0, bits: 16), Width(bits: 16)] // IVT
                public uint InnerVlanTag;
                [PacketField, Offset(doubleWords: 1, bits: 0), Width(bits: 3)] // PT
                public PayloadType PayloadType;
                [PacketField, Offset(doubleWords: 1, bits: 3), Width(bits: 1)] // IPHE
                public bool IpHeaderError;
                [PacketField, Offset(doubleWords: 1, bits: 4), Width(bits: 1)] // IPV4
                public bool Ipv4HeaderPresent;
                [PacketField, Offset(doubleWords: 1, bits: 5), Width(bits: 1)] // IPV6
                public bool Ipv6HeaderPresent;
                [PacketField, Offset(doubleWords: 1, bits: 6), Width(bits: 1)] // IPCB
                public bool IpChecksumBypassed;
                [PacketField, Offset(doubleWords: 1, bits: 7), Width(bits: 1)] // IPCE
                public bool IpPayloadError;
                [PacketField, Offset(doubleWords: 1, bits: 8), Width(bits: 4)] // PMT
                public PTPMessageType PtpMessageType;
                [PacketField, Offset(doubleWords: 1, bits: 12), Width(bits: 1)] // PFT
                public bool PtpPacketType;
                [PacketField, Offset(doubleWords: 1, bits: 13), Width(bits: 1)] // PV
                public PTPVersion PtpVersion;
                [PacketField, Offset(doubleWords: 1, bits: 14), Width(bits: 1)] // TSA
                public bool TimestampAvailable;
                [PacketField, Offset(doubleWords: 1, bits: 15), Width(bits: 1)] // TD
                public bool TimestampDropped;
                [PacketField, Offset(doubleWords: 1, bits: 16), Width(bits: 16)] // OPC
                public uint OamSubtypeCodeOrMACControlPacketOpcode;
                // bits 0:9 of 3rd double word are reserved
                [PacketField, Offset(doubleWords: 2, bits: 10), Width(bits: 1)] // ARPNR
                public bool ArpReplyNotGenerated;
                // bits 11:14 of 3rd double word are reserved
                [PacketField, Offset(doubleWords: 2, bits: 15), Width(bits: 1)] // VF
                public bool VlanFiletrStatus;
                [PacketField, Offset(doubleWords: 2, bits: 16), Width(bits: 1)] // SAF
                public bool SourceAddressFilterFail;
                [PacketField, Offset(doubleWords: 2, bits: 17), Width(bits: 1)] // DAF
                public bool DestinaltionAddressFilterFail;
                [PacketField, Offset(doubleWords: 2, bits: 18), Width(bits: 1)] // HF
                public bool HashFilterStatus;
                [PacketField, Offset(doubleWords: 2, bits: 19), Width(bits: 8)] // MADRM
                public bool MacAddressMatchOrHashValue;
                [PacketField, Offset(doubleWords: 2, bits: 27), Width(bits: 1)] // L3FM
                public bool Layer3FilterMatch;
                [PacketField, Offset(doubleWords: 2, bits: 28), Width(bits: 1)] // L4FM
                public bool Layer4FilterMatch;
                [PacketField, Offset(doubleWords: 2, bits: 29), Width(bits: 3)] // L3L4FM
                public uint MatchedFilterNumber;
                [PacketField, Offset(doubleWords: 3, bits: 0), Width(bits: 15)] // PL
                public uint PacketLength;
                [PacketField, Offset(doubleWords: 3, bits: 15), Width(bits: 1)] // ES
                public bool ErrorSummary;
                [PacketField, Offset(doubleWords: 3, bits: 16), Width(bits: 3)] // LT
                public PacketKind LengthTypeField;
                [PacketField, Offset(doubleWords: 3, bits: 19), Width(bits: 1)] // DE
                public bool DribbleBitError;
                [PacketField, Offset(doubleWords: 3, bits: 20), Width(bits: 1)] // RE
                public bool ReceiveError;
                [PacketField, Offset(doubleWords: 3, bits: 21), Width(bits: 1)] // OE
                public bool OverflowError;
                [PacketField, Offset(doubleWords: 3, bits: 22), Width(bits: 1)] // RWT
                public bool ReceiveWatchdogTimeout;
                [PacketField, Offset(doubleWords: 3, bits: 23), Width(bits: 1)] // GP
                public bool GiantPacket;
                [PacketField, Offset(doubleWords: 3, bits: 24), Width(bits: 1)] // CE
                public bool CrcError;
                [PacketField, Offset(doubleWords: 3, bits: 25), Width(bits: 1)] // RS0V
                public bool ReceiveStatusSegment0Valid;
                [PacketField, Offset(doubleWords: 3, bits: 26), Width(bits: 1)] // RS1V
                public bool ReceiveStatusSegment1Valid;
                [PacketField, Offset(doubleWords: 3, bits: 27), Width(bits: 1)] // RS2V
                public bool ReceiveStatusSegment2Valid;
                [PacketField, Offset(doubleWords: 3, bits: 28), Width(bits: 1)] // LD
                public bool LastDescriptor;
                [PacketField, Offset(doubleWords: 3, bits: 29), Width(bits: 1)] // FD
                public bool FirstDescriptor;
                [PacketField, Offset(doubleWords: 3, bits: 30), Width(bits: 1)] // CTXT
                public bool ContextTypeField;
                [PacketField, Offset(doubleWords: 3, bits: 31), Width(bits: 1)] // OWN
                public DescriptorOwner OwnerField;
#pragma warning restore 649
            }

            [LeastSignificantByteFirst]
            public struct ContextDescriptor : IDescriptorStruct
            {
                public override string ToString()
                {
                    return PrettyString;
                }

                public string PrettyString => $@"ContextRxDescriptor {{
    receivePacketTimestamp: 0x{ReceivePacketTimestamp:X},
    contextType: {ContextTypeField},
    owner: {OwnerField}
}}";

                public bool ContextType => ContextTypeField;

                public DescriptorOwner Owner => OwnerField;

#pragma warning disable 649
                [PacketField, Offset(quadWords: 0), Width(bits: 64)] // RTS
                public ulong ReceivePacketTimestamp;
                // 3nd double word and bits 0:29 of 4th double word are reserved
                [PacketField, Offset(doubleWords: 3, bits: 30), Width(bits: 1)] // CTXT
                public bool ContextTypeField;
                [PacketField, Offset(doubleWords: 3, bits: 31), Width(bits: 1)] // OWN
                public DescriptorOwner OwnerField;
#pragma warning restore 649
            }
        }

        private class TxDescriptor : Descriptor
        {
            public TxDescriptor(IBusController bus, ulong address, IPeripheral context = null) : base(bus, address, context)
            {
            }

            public override void Fetch()
            {
                base.Fetch();
                type = isContextDescriptor.Value ? typeof(ContextDescriptor) : typeof(NormalReadDescriptor);
            }

            public NormalReadDescriptor GetNormalReadDescriptor()
            {
                return Packet.Decode<NormalReadDescriptor>(data);
            }

            public ContextDescriptor GetContextDescriptor()
            {
                return Packet.Decode<ContextDescriptor>(data);
            }

            [LeastSignificantByteFirst]
            public struct NormalReadDescriptor : IDescriptorStruct
            {
                public override string ToString()
                {
                    return PrettyString;
                }

                public byte[] FetchBuffer1OrHeader(IBusController bus, IPeripheral context = null)
                {
                    var data = new byte[HeaderOrBuffer1Length];
                    bus.ReadBytes((ulong)Buffer1OrHeaderAddress, (int)HeaderOrBuffer1Length, data, 0, true, context);
                    return data;
                }

                public byte[] FetchBuffer2OrBuffer1(IBusController bus, IPeripheral context = null)
                {
                    var data = new byte[Buffer2Length];
                    bus.ReadBytes((ulong)Buffer2orBuffer1Address, (int)Buffer2Length, data, 0, true, context);
                    return data;
                }

                public uint PayloadLength
                {
                    get
                    {
                        return PayloadLengthLower15bits | PayloadLength15bit << 15 | (uint)ChecksumControl << 16;
                    }
                }

                public string PrettyString => $@"NormalReadTxDescriptor {{
    buffer1OrHeaderAddress: 0x{Buffer1OrHeaderAddress:X},
    buffer2orBuffer1Address: 0x{Buffer2orBuffer1Address:X},
    headerOrBuffer1Length: 0x{HeaderOrBuffer1Length:X},
    vlanTagControl: {VlanTagControl},
    buffer2Length: 0x{Buffer2Length:X},
    transmitTimestampEnable: {TransmitTimestampEnable},
    interruptOnCompletion: {InterruptOnCompletion},
    payloadLengthLower15bits: {PayloadLengthLower15bits},
    payloadLength15bit: {PayloadLength15bit},
    checksumControl: {ChecksumControl},
    tcpSegmentationEnable: {TcpSegmentationEnable},
    headerLength: {HeaderLength},
    sourceAddressControl: {SourceAddressControl},
    crcPadControl: {CrcPadControl},
    lastDescriptor: {LastDescriptor},
    firstDescriptor: {FirstDescriptor},
    contextType: {ContextTypeField},
    owner: {OwnerField}
}}";

                public bool ContextType => ContextTypeField;

                public DescriptorOwner Owner => OwnerField;

#pragma warning disable 649
                [PacketField, Offset(doubleWords: 0, bits: 0), Width(bits: 32)] // BUF1AP
                public uint Buffer1OrHeaderAddress;
                [PacketField, Offset(doubleWords: 1, bits: 0), Width(bits: 32)] // BUF2AP
                public uint Buffer2orBuffer1Address;
                [PacketField, Offset(doubleWords: 2, bits: 0), Width(bits: 14)] // HR/B1L
                public uint HeaderOrBuffer1Length;
                [PacketField, Offset(doubleWords: 2, bits: 14), Width(bits: 2)] // VTIR
                public VLANTagOperation VlanTagControl;
                [PacketField, Offset(doubleWords: 2, bits: 16), Width(bits: 14)] // B2L
                public uint Buffer2Length;
                [PacketField, Offset(doubleWords: 2, bits: 30), Width(bits: 1)] // TTSE
                public bool TransmitTimestampEnable;
                [PacketField, Offset(doubleWords: 2, bits: 31), Width(bits: 1)] // IOC
                public bool InterruptOnCompletion;
                [PacketField, Offset(doubleWords: 3, bits: 0), Width(bits: 15)] // FL/TPL
                public uint PayloadLengthLower15bits;
                [PacketField, Offset(doubleWords: 3, bits: 15), Width(bits: 1)] // TPL
                public uint PayloadLength15bit;
                [PacketField, Offset(doubleWords: 3, bits: 16), Width(bits: 2)] // CIC/TPL
                public ChecksumOperation ChecksumControl;
                [PacketField, Offset(doubleWords: 3, bits: 18), Width(bits: 1)] // TSE
                public bool TcpSegmentationEnable;
                [PacketField, Offset(doubleWords: 3, bits: 19), Width(bits: 4)] // THL
                public uint HeaderLength;
                [PacketField, Offset(doubleWords: 3, bits: 23), Width(bits: 3)] // SAIC
                public DescriptorSourceAddressOperation SourceAddressControl;
                [PacketField, Offset(doubleWords: 3, bits: 26), Width(bits: 2)] // CPC
                public CRCPadOperation CrcPadControl;
                [PacketField, Offset(doubleWords: 3, bits: 28), Width(bits: 1)] // LD
                public bool LastDescriptor;
                [PacketField, Offset(doubleWords: 3, bits: 29), Width(bits: 1)] // FD
                public bool FirstDescriptor;
                [PacketField, Offset(doubleWords: 3, bits: 30), Width(bits: 1)] // CTXT
                public bool ContextTypeField;
                [PacketField, Offset(doubleWords: 3, bits: 31), Width(bits: 1)] // OWN
                public DescriptorOwner OwnerField;
#pragma warning restore 649
            }

            [LeastSignificantByteFirst]
            public struct NormalWriteBackDescriptor : IDescriptorStruct
            {
                public override string ToString()
                {
                    return PrettyString;
                }

                public string PrettyString => $@"NormalWriteBackTxDescriptor {{
    txPacketTimestamp: 0x{TxPacketTimestamp:X},
    ipHeaderError: {IpHeaderError},
    deferredBit: {DeferredBit},
    underflowError: {UnderflowError},
    excessiveDeferral: {ExcessiveDeferral},
    collisionCount: {CollisionCount},
    excessiveCollision: {ExcessiveCollision},
    lateCollision: {LateCollision},
    noCarrier: {NoCarrier},
    lossOfCarrier: {LossOfCarrier},
    payloadChecksumError: {PayloadChecksumError},
    packetFlushed: {PacketFlushed},
    jabberTimeout: {JabberTimeout},
    errorSummary: {ErrorSummary},
    txTimestampCaptured: {TxTimestampCaptured},
    lastDescriptor: {LastDescriptor},
    firstDescriptor: {FirstDescriptor},
    contextType: {ContextTypeField},
    owner: {OwnerField}
}}";

                public bool ContextType => ContextTypeField;

                public DescriptorOwner Owner => OwnerField;

#pragma warning disable 649
                [PacketField, Offset(quadWords: 0), Width(bits: 64)] // TTS
                public ulong TxPacketTimestamp;
                // 3nd double word is reserved
                [PacketField, Offset(doubleWords: 3, bits: 0), Width(bits: 1)] // IHE
                public bool IpHeaderError;
                [PacketField, Offset(doubleWords: 3, bits: 1), Width(bits: 1)] // DB
                public bool DeferredBit;
                [PacketField, Offset(doubleWords: 3, bits: 2), Width(bits: 1)] // UF
                public bool UnderflowError;
                [PacketField, Offset(doubleWords: 3, bits: 3), Width(bits: 1)] // ED
                public bool ExcessiveDeferral;
                [PacketField, Offset(doubleWords: 3, bits: 4), Width(bits: 4)] // CC
                public bool CollisionCount;
                [PacketField, Offset(doubleWords: 3, bits: 8), Width(bits: 1)] // EC
                public bool ExcessiveCollision;
                [PacketField, Offset(doubleWords: 3, bits: 9), Width(bits: 1)] // LC
                public bool LateCollision;
                [PacketField, Offset(doubleWords: 3, bits: 10), Width(bits: 1)] // NC
                public bool NoCarrier;
                [PacketField, Offset(doubleWords: 3, bits: 11), Width(bits: 1)] // LoC
                public bool LossOfCarrier;
                [PacketField, Offset(doubleWords: 3, bits: 12), Width(bits: 1)] // PCE
                public bool PayloadChecksumError;
                [PacketField, Offset(doubleWords: 3, bits: 13), Width(bits: 1)] // FF
                public bool PacketFlushed;
                [PacketField, Offset(doubleWords: 3, bits: 14), Width(bits: 1)] // JT
                public bool JabberTimeout;
                [PacketField, Offset(doubleWords: 3, bits: 15), Width(bits: 1)] // ES
                public bool ErrorSummary;
                // bit 16 of 4th double word is reserved
                [PacketField, Offset(doubleWords: 3, bits: 17), Width(bits: 1)] // TTSS
                public bool TxTimestampCaptured;
                // bits 18:27 of 4th double word are reserved
                [PacketField, Offset(doubleWords: 3, bits: 28), Width(bits: 1)] // LD
                public bool LastDescriptor;
                [PacketField, Offset(doubleWords: 3, bits: 29), Width(bits: 1)] // FD
                public bool FirstDescriptor;
                [PacketField, Offset(doubleWords: 3, bits: 30), Width(bits: 1)] // CTXT
                public bool ContextTypeField;
                [PacketField, Offset(doubleWords: 3, bits: 31), Width(bits: 1)] // OWN
                public DescriptorOwner OwnerField;
#pragma warning restore 649
            }

            [LeastSignificantByteFirst]
            public struct ContextDescriptor : IDescriptorStruct
            {
                public override string ToString()
                {
                    return PrettyString;
                }

                public string PrettyString => $@"ContextTxDescriptor {{
    txPacketTimestamp: {TxPacketTimestamp},
    maximumSegmentSize: {MaximumSegmentSize},
    innerVlanTag: {InnerVlanTag},
    vlanTag: {VlanTag},
    vlanTagValid: {VlanTagValid},
    innerVlanTagValid: {InnerVlanTagValid},
    innerVlanTagControl: {InnerVlanTagControl},
    contextDescriptorError: {ContextDescriptorError},
    oneStepTimestampCorrectionInputOrMaximumSegmentSizeValid: {OneStepTimestampCorrectionInputOrMaximumSegmentSizeValid},
    oneStepTimestampCorrectionEnable: {OneStepTimestampCorrectionEnable},
    contextType: {ContextTypeField},
    owner: {OwnerField}
}}";

                public bool ContextType => ContextTypeField;

                public DescriptorOwner Owner => OwnerField;

#pragma warning disable 649
                [PacketField, Offset(quadWords: 0), Width(bits: 64)] // TTS
                public ulong TxPacketTimestamp;
                [PacketField, Offset(doubleWords: 2, bits: 0), Width(bits: 14)] // MSS
                public uint MaximumSegmentSize;
                // bits 14:15 of 3rd double word are reserved
                [PacketField, Offset(doubleWords: 2, bits: 16), Width(bits: 16)] // IVT
                public uint InnerVlanTag;
                [PacketField, Offset(doubleWords: 3, bits: 0), Width(bits: 16)] // VT
                public uint VlanTag;
                [PacketField, Offset(doubleWords: 3, bits: 16), Width(bits: 1)] // VLTV
                public bool VlanTagValid;
                [PacketField, Offset(doubleWords: 3, bits: 17), Width(bits: 1)] // IVLTV
                public bool InnerVlanTagValid;
                [PacketField, Offset(doubleWords: 3, bits: 18), Width(bits: 2)] // IVTIR
                public VLANTagOperation InnerVlanTagControl;
                // bits 20:22 of 4th double word are reserved
                [PacketField, Offset(doubleWords: 3, bits: 23), Width(bits: 1)] // CDE
                public bool ContextDescriptorError;
                // bits 24:25 of 4th double word are reserved
                [PacketField, Offset(doubleWords: 3, bits: 26), Width(bits: 1)] // TCMSSV
                public bool OneStepTimestampCorrectionInputOrMaximumSegmentSizeValid;
                [PacketField, Offset(doubleWords: 3, bits: 27), Width(bits: 1)] // OSTC
                public bool OneStepTimestampCorrectionEnable;
                // bits 28:29 of 4th double word are reserved
                [PacketField, Offset(doubleWords: 3, bits: 30), Width(bits: 1)] // CTXT
                public bool ContextTypeField;
                [PacketField, Offset(doubleWords: 3, bits: 31), Width(bits: 1)] // OWN
                public DescriptorOwner OwnerField;
#pragma warning restore 649
            }
        }

        private abstract class Descriptor
        {
            public static uint Size => 0x10;

            public Descriptor(IBusController bus, ulong address, IPeripheral context = null)
            {
                this.bus = bus;
                this.context = context;
                Address = address;
                this.data = new byte[Size];
            }

            public virtual void Fetch()
            {
                bus.ReadBytes(Address, (int)Size, data, 0, true, context);
                var structure = Packet.Decode<MinimalCommonDescriptor>(data);
                UpdateProperties(structure);
            }

            public void Write()
            {
                bus.WriteBytes(data, Address, context: context);
            }

            public void SetDescriptor<T>(T structure) where T : IDescriptorStruct
            {
                type = typeof(T);
                data = Packet.Encode<T>(structure);
                UpdateProperties(structure);
            }

            public Type Type => type;

            public ulong Address { get; }

            public bool? IsOwnedByDMA { get; protected set; }

            protected void UpdateProperties(IDescriptorStruct structure)
            {
                IsOwnedByDMA = structure.Owner == DescriptorOwner.DMA;
                isContextDescriptor = structure.ContextType;
            }

            protected bool? isContextDescriptor;
            protected Type type;
            protected byte[] data;

            private readonly IBusController bus;
            private readonly IPeripheral context;

            public interface IDescriptorStruct
            {
                bool ContextType { get; }

                DescriptorOwner Owner { get; }
            }

            [LeastSignificantByteFirst]
            private struct MinimalCommonDescriptor : IDescriptorStruct
            {
                public bool ContextType => ContextTypeField;

                public DescriptorOwner Owner => OwnerField;

#pragma warning disable 649
                [PacketField, Offset(doubleWords: 3, bits: 30), Width(bits: 1)] // CTXT
                public bool ContextTypeField;
                [PacketField, Offset(doubleWords: 3, bits: 31), Width(bits: 1)] // OWN
                public DescriptorOwner OwnerField;
#pragma warning restore 649
            }
        }

        private enum DescriptorOwner : byte
        {
            DMA = 1,
            Application = 0,
        }

        private enum PayloadType : byte
        {
            Unknown  = 0b000,
            UDP      = 0b001,
            TCP      = 0b010,
            ICMP     = 0b011,
            IGMPIPV4 = 0b100,
        }

        private enum PTPMessageType : byte
        {
            NoPTPMessageReceived             = 0b0000,
            Sync                             = 0b0001,
            FollowUp                         = 0b0010,
            DelayRequest                     = 0b0011,
            DelayResponse                    = 0b0100,
            PdelayRequest                    = 0b0101,
            PdelayResponse                   = 0b0110,
            PdelayResponseFollowUp           = 0b0111,
            Announce                         = 0b1000,
            Management                       = 0b1001,
            Signaling                        = 0b1010,
            // Reserved                      = 0b1011..0b1110,
            PTPPacketWithReservedMessageType = 0b1111,
        }

        private enum PTPVersion : byte
        {
            IEEE1588version2 = 1,
            IEEE1588version1 = 0,
        }

        private enum PacketKind : byte
        {
            LengthPacket                = 0b000,
            TypePacket                  = 0b001,
            // Reserved                 = 0b010,
            ARPRequest                  = 0b011,
            TypePacketWithVLANTag       = 0b100,
            TypePacketWithDoubleVLANTag = 0b101,
            MACControlPacket            = 0b110,
            OAMPacket                   = 0b111,
        }

        private enum VLANTagOperation : byte
        {
            None    = 0b00,
            Remove  = 0b01,
            Insert  = 0b10,
            Replace = 0b11,
        }

        private enum ChecksumOperation : byte
        {
            None                                       = 0b00,
            InsertHeaderChecksum                       = 0b01,
            InsertHeaderAndPayloadChecksum             = 0b10,
            InsertHeaderPayloadAndPseudoHeaderChecksum = 0b11,
        }

        private enum DescriptorSourceAddressOperation : byte
        {
            MACAddressRegister0None     = 0b000,
            MACAddressRegister0Insert   = 0b001,
            MACAddressRegister0Replace  = 0b010,
            MACAddressRegister0Reserved = 0b011,
            MACAddressRegister1None     = 0b100,
            MACAddressRegister1Insert   = 0b101,
            MACAddressRegister1Replace  = 0b110,
            MACAddressRegister1Reserved = 0b111,
        }

        private enum CRCPadOperation : byte
        {
            InsetCRCAndPad = 0b00,
            InsertCRC      = 0b01,
            None           = 0b10,
            ReplaceCRC     = 0b11,
        }
    }
}

namespace Antmicro.Renode.Peripherals.Network
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net.NetworkInformation;
    using Antmicro.Renode.Core.Structure;
    using Antmicro.Renode.Logging;
    using Antmicro.Renode.Network;
    using MiscUtil.Conversion;
    using PacketDotNet;
    using IPProtocolType = Antmicro.Renode.Network.IPProtocolType;

    public partial class NXP_ENET_QOS
    {
        private static readonly EtherType[] checksumOffloadEngineIpHeaderTypes =
        {
            EtherType.IpV4,
        };

        private static readonly IPProtocolType[] checksumOffloadEnginePseudoHeaderTypes =
        {
            IPProtocolType.TCP,
            IPProtocolType.UDP,
            IPProtocolType.ICMP,
        };

        private class FrameAssembler
        {
            public FrameAssembler(IEmulationElement parent, CRCPadOperation crcPadControl, ChecksumOperation checksumControl, Action<EthernetFrame> frameReady)
                : this(parent, null, crcPadControl, checksumControl, 0, frameReady, null)
            {
            }

            public FrameAssembler(IEmulationElement parent, byte[] header, uint defaultMaximumSegmentSize, TxDescriptor.ContextDescriptor? context, bool enableChecksumOffload, Action<EthernetFrame> frameReady, MACAddress? sourceMACAddress)
                : this(parent, header, CRCPadOperation.InsetCRCAndPad, enableChecksumOffload ? ChecksumOperation.InsertHeaderPayloadAndPseudoHeaderChecksum : ChecksumOperation.None,
                    context.HasValue && context.Value.OneStepTimestampCorrectionInputOrMaximumSegmentSizeValid ? context.Value.MaximumSegmentSize : defaultMaximumSegmentSize, frameReady, sourceMACAddress)
            {
                if(header.Length < EthernetFields.HeaderLength)
                {
                    parent.Log(LogLevel.Error, "TCP Segmentation Offload: Header is too small, the result may be unpredictable.");
                    header = null;
                    return;
                }
                etherType = (EtherType)EndianBitConverter.Big.ToInt16(header, EthernetFields.TypePosition);
                if(etherType != EtherType.IpV4 && etherType != EtherType.IpV6)
                {
                    parent.Log(LogLevel.Error, "TCP Segmentation Offload: Invalid Ethernet Type, the result may be unpredictable.");
                    return;
                }
                if(header.Length < EthernetFields.HeaderLength + (etherType == EtherType.IpV4 ? IPv4Fields.HeaderLength : IPv6Fields.HeaderLength))
                {
                    parent.Log(LogLevel.Error, "TCP Segmentation Offload: Header is too small, the result may be unpredictable.");
                    header = null;
                    return;
                }
            }

            public void PushPayload(byte[] payloadSegment)
            {
                if(payloadSegment.Length == 0)
                {
                    return;
                }
                totalPayloadLength += (uint)payloadSegment.Length;
                if(!SegmentationActive || totalPayloadLength < maximumSegmentSize || maximumSegmentSize == 0)
                {
                    payloadSegments.Enqueue(payloadSegment);
                    return;
                }
                totalPayloadLength %= maximumSegmentSize;
                var saveLast = totalPayloadLength != 0;

                // Divide payload into MSS segments + optional reminding segment
                var i = 0;
                var segments = payloadSegments
                    .SelectMany(x => x) // flatten
                    .Concat(payloadSegment)
                    .GroupBy(_ => i++ / maximumSegmentSize) // group into MSS chunks
                    .ToArray();

                payloadSegments.Clear();

                if(saveLast)
                {
                    payloadSegments.Enqueue(segments.Last().ToArray());
                }

                foreach(var segment in segments.Take(segments.Length + (saveLast ? -1 : 0)))
                {
                    FinalizeSegment(segment, maximumSegmentSize);
                }
            }

            public void FinalizeAssembly()
            {
                if(totalPayloadLength == 0)
                {
                    return;
                }
                FinalizeSegment(payloadSegments.SelectMany(x => x), totalPayloadLength, true);
            }

            private FrameAssembler(IEmulationElement parent, byte[] header, CRCPadOperation crcPadControl, ChecksumOperation checksumControl, uint maximumSegmentSize, Action<EthernetFrame> frameReady, MACAddress? sourceMACAddress)
            {
                this.parent = parent;
                tcpHeader = header;
                this.sourceMACAddress = sourceMACAddress;

                padEthernetFrame = false;

                switch(crcPadControl)
                {
                case CRCPadOperation.ReplaceCRC:
                    crcMode = CRCMode.Replace;
                    break;
                case CRCPadOperation.None:
                    crcMode = CRCMode.Keep;
                    break;
                case CRCPadOperation.InsetCRCAndPad:
                    padEthernetFrame = true;
                    goto case CRCPadOperation.InsertCRC;
                case CRCPadOperation.InsertCRC:
                    crcMode = CRCMode.Add;
                    break;
                default:
                    throw new Exception("Unreachable");
                }

                switch(checksumControl)
                {
                case ChecksumOperation.None:
                    break;
                case ChecksumOperation.InsertHeaderChecksum:
                    checksumTypes = null;
                    break;
                case ChecksumOperation.InsertHeaderAndPayloadChecksum:
                    parent.Log(LogLevel.Warning, "Checksum Insertion Control: Calculating checksum with precalculated pseudo-header (0b10) is not supported. Falling back to calculating checksum with pseduo-header (0b11).");
                    checksumControl = ChecksumOperation.InsertHeaderAndPayloadChecksum;
                    goto case ChecksumOperation.InsertHeaderPayloadAndPseudoHeaderChecksum;
                case ChecksumOperation.InsertHeaderPayloadAndPseudoHeaderChecksum:
                    checksumTypes = checksumOffloadEnginePseudoHeaderTypes;
                    break;
                default:
                    throw new Exception("Unreachable");
                }
                checksumOp = checksumControl;

                if(SegmentationActive && maximumSegmentSize == 0)
                {
                    parent.Log(LogLevel.Error, "TCP Segmentation Offload: Ignoring invalid Maximum Segment Size value: {0}", maximumSegmentSize);
                }
                this.maximumSegmentSize = maximumSegmentSize;

                this.frameReady = frameReady;
                payloadSegments = new Queue<byte[]>();
            }

            private void FinalizeSegment(IEnumerable<byte> frame, uint length, bool isLast = false)
            {
                if(TryCreateEthernetFrame(tcpHeader?.Concat(frame) ?? frame, length + (uint?)tcpHeader?.Length ?? 0, out var builtFrame, isLast))
                {
                    frameReady(builtFrame);
                    packetsFinalized += 1;
                }
                else
                {
                    parent.Log(LogLevel.Error, "Failed to create EthernetFrame");
                }
            }

            private bool TryCreateEthernetFrame(IEnumerable<byte> frame, uint length, out EthernetFrame builtFrame, bool isLast)
            {
                if(padEthernetFrame && length < MinimalLength)
                {
                    frame = frame.Concat(Enumerable.Repeat<byte>(0, MinimalLength - (int)length));
                }
                var frameArray = frame.ToArray();
                if(SegmentationActive)
                {
                    // Update length field before packet creation to workaround Packet.Net asserts
                    var ipLength = length - EthernetFields.HeaderLength;
                    if(etherType == EtherType.IpV4)
                    {
                        EndianBitConverter.Big.CopyBytes((ushort)ipLength, frameArray, EthernetFields.HeaderLength + IPv4Fields.TotalLengthPosition);
                    }
                    else if(etherType == EtherType.IpV6)
                    {
                        var ipv6length = length - EthernetFields.HeaderLength;
                        EndianBitConverter.Big.CopyBytes((ushort)ipLength, frameArray, EthernetFields.HeaderLength + IPv6Fields.PayloadLengthPosition);
                    }
                }
                try
                {
                    if(!EthernetFrame.TryCreateEthernetFrame(frameArray, crcMode, out builtFrame))
                    {
                        return false;
                    }
                    if(SegmentationActive && builtFrame.UnderlyingPacket.PayloadPacket.PayloadPacket is TcpPacket tcpPacket)
                    {
                        var isFirst = packetsFinalized == 0;
                        if(!isFirst && etherType == EtherType.IpV4)
                        {
                            ((IPv4Packet)builtFrame.UnderlyingPacket.PayloadPacket).Id += (ushort)packetsFinalized;
                        }
                        if(!isFirst)
                        {
                            tcpPacket.SequenceNumber += packetsFinalized * maximumSegmentSize;
                        }
                        if(!isLast)
                        {
                            tcpPacket.Fin = false;
                            tcpPacket.Psh = false;
                        }
                    }
                    if(checksumOp != ChecksumOperation.None)
                    {
                        builtFrame.FillWithChecksums(checksumOffloadEngineIpHeaderTypes, checksumTypes, crcMode != CRCMode.Keep);
                    }
                    if(sourceMACAddress.HasValue)
                    {
                        builtFrame.UnderlyingPacket.SourceHwAddress = (PhysicalAddress)sourceMACAddress.Value;
                    }
                    return true;
                }
                catch(Exception e)
                {
                    builtFrame = null;
                    parent.Log(LogLevel.Error, "Underlying packet processing framework failed to create Ethernet frame: {0}", e);
                }
                return false;
            }

            private bool SegmentationActive => tcpHeader != null;

            private uint totalPayloadLength;
            private uint packetsFinalized;

            private readonly IEmulationElement parent;
            private readonly MACAddress? sourceMACAddress;
            private readonly byte[] tcpHeader;
            private readonly CRCMode crcMode;
            private readonly bool padEthernetFrame;
            private readonly ChecksumOperation checksumOp;
            private readonly IPProtocolType[] checksumTypes;
            private readonly uint maximumSegmentSize;
            private readonly Action<EthernetFrame> frameReady;
            private readonly Queue<byte[]> payloadSegments;
            private readonly EtherType etherType;

            private const int MinimalLength = 60;
        }
    }
}
namespace Antmicro.Renode.Peripherals.Network
{
    using System;
    using System.Collections.Generic;
    using Antmicro.Renode.Core.Structure.Registers;
    using Antmicro.Renode.Logging;
    using Antmicro.Renode.Peripherals.Bus;
    using Antmicro.Renode.Peripherals.Bus.Wrappers;
    using Antmicro.Renode.UserInterface;
    using Antmicro.Renode.Utilities;

    public partial class NXP_ENET_QOS : IDoubleWordPeripheral
    {
        public virtual uint ReadDoubleWord(long offset)
        {
            return Read<RegistersMacAndMmc>(macAndMmcRegisters, "MAC and MMC", offset);
        }

        public virtual void WriteDoubleWord(long offset, uint value)
        {
            Write<RegistersMacAndMmc>(macAndMmcRegisters, "MAC and MMC", offset, value);
        }

        [ConnectionRegion("mtl")]
        public uint ReadDoubleWordFromMTL(long offset)
        {
            return Read<RegistersMTL>(mtlRegisters, "MTL", offset);
        }

        [ConnectionRegion("mtl")]
        public void WriteDoubleWordToMTL(long offset, uint value)
        {
            Write<RegistersMTL>(mtlRegisters, "MTL", offset, value);
        }

        [ConnectionRegion("dma")]
        public uint ReadDoubleWordFromDMA(long offset)
        {
            return Read<RegistersDMA>(dmaRegisters, "DMA", offset);
        }

        [ConnectionRegion("dma")]
        public void WriteDoubleWordToDMA(long offset, uint value)
        {
            Write<RegistersDMA>(dmaRegisters, "DMA", offset, value);
        }

        [UiAccessible]
        public string[,] GetCoutersInfo()
        {
            var table = new Table();
            table.AddRow("Name", "Value");
            table.AddRow(nameof(txByteCounter), Convert.ToString(txByteCounter.Value));
            table.AddRow(nameof(txPacketCounter), Convert.ToString(txPacketCounter.Value));
            table.AddRow(nameof(txUnicastPacketCounter), Convert.ToString(txUnicastPacketCounter.Value));
            table.AddRow(nameof(txMulticastPacketCounter), Convert.ToString(txMulticastPacketCounter.Value));
            table.AddRow(nameof(txBroadcastPacketCounter), Convert.ToString(txBroadcastPacketCounter.Value));
            table.AddRow(nameof(txGoodByteCounter), Convert.ToString(txGoodByteCounter.Value));
            table.AddRow(nameof(txGoodPacketCounter), Convert.ToString(txGoodPacketCounter.Value));
            table.AddRow(nameof(rxPacketCounter), Convert.ToString(rxPacketCounter.Value));
            table.AddRow(nameof(rxByteCounter), Convert.ToString(rxByteCounter.Value));
            table.AddRow(nameof(rxGoodByteCounter), Convert.ToString(rxGoodByteCounter.Value));
            table.AddRow(nameof(rxBroadcastPacketCounter), Convert.ToString(rxBroadcastPacketCounter.Value));
            table.AddRow(nameof(rxMulticastPacketCounter), Convert.ToString(rxMulticastPacketCounter.Value));
            table.AddRow(nameof(rxCrcErrorPacketCounter), Convert.ToString(rxCrcErrorPacketCounter.Value));
            table.AddRow(nameof(rxUnicastPacketCounter), Convert.ToString(rxUnicastPacketCounter.Value));
            table.AddRow(nameof(rxFifoPacketCounter), Convert.ToString(rxFifoPacketCounter.Value));
            for(var i = 0; i < NumberOfIpcCounters; i++)
            {
                var name = (IpcCounter)i;
                table.AddRow($"IPC {name} Packets", Convert.ToString(rxIpcPacketCounter[i].Value));
                table.AddRow($"IPC {name} Bytes", Convert.ToString(rxIpcByteCounter[i].Value));
            }
            return table.ToArray();
        }

        // This property works similar to `sysbus LogPeripheralAccess` command,
        // but supports regions. Setting this to `null` disables logging.
        public LogLevel PeripheralAccessLogLevel { get; set; } = null;

        protected IEnumRegisterField<DMAChannelInterruptMode> dmaInterruptMode;

        private void ResetRegisters()
        {
            macAndMmcRegisters.Reset();
            mtlRegisters.Reset();
            dmaRegisters.Reset();
        }

        private IDictionary<long, DoubleWordRegister> CreateRegisterMap()
        {
            return new Dictionary<long, DoubleWordRegister>()
            {
                {(long)RegistersMacAndMmc.OperatingModeConfiguration, new DoubleWordRegister(this)
                    .WithFlag(0, out rxEnable, name: "MACCR.RE (Receiver Enable)")
                    .WithFlag(1, out txEnable, name: "MACCR.TE (TE)")
                    .WithTag("MACCR.PRELEN (PRELEN)", 2, 2)
                    .WithTaggedFlag("MACCR.DC (DC)", 4)
                    .WithTag("MACCR.BL (BL)", 5, 2)
                    .WithReservedBits(7, 1)
                    .WithTaggedFlag("MACCR.DR (DR)", 8)
                    .WithTaggedFlag("MACCR.DCRS (DCRS)", 9)
                    .WithTaggedFlag("MACCR.DO (DO)", 10)
                    .WithTaggedFlag("MACCR.ECRSFD (ECRSFD)", 11)
                    .WithFlag(12, out loopbackEnabled, name: "MACCR.LM (LM)")
                    .WithTaggedFlag("MACCR.DM (DM)", 13)
                    .WithTaggedFlag("MACCR.FES (FES)", 14)
                    .WithReservedBits(15, 1)
                    .WithTaggedFlag("MACCR.JD (JD)", 17)
                    .WithTaggedFlag("MACCR.JE (JE)", 16)
                    .WithReservedBits(18, 1)
                    .WithTaggedFlag("MACCR.WD (WD)", 19)
                    .WithTaggedFlag("MACCR.ACS (ACS)", 20)
                    .WithTaggedFlag("MACCR.CST (CST)", 21)
                    .WithTaggedFlag("MACCR.S2KP (S2KP)", 22)
                    .WithTaggedFlag("MACCR.GPSLCE (GPSLCE)", 23)
                    .WithTag("MACCR.IPG (IPG)", 24, 3)
                    .WithFlag(27, out checksumOffloadEnable, name: "MACCR.IPC (IPC)")
                    .WithEnumField<DoubleWordRegister, RegisterSourceAddressOperation>(28, 3, out sourceAddressOperation, name: "MACCR.SARC (SARC)")
                    .WithTaggedFlag("MACCR.ARPEN (ARPEN)", 31)
                },
                {(long)RegistersMacAndMmc.ExtendedOperatingModeConfiguration, new DoubleWordRegister(this)
                    .WithTag("MACECR.GPSL (GPSL)", 0, 14)
                    .WithReservedBits(14, 2)
                    .WithFlag(16, out crcCheckDisable, name: "MACECR.DCRCC (DCRCC)")
                    .WithTaggedFlag("MACECR.SPEN (SPEN)", 17)
                    .WithTaggedFlag("MACECR.USP (USP)", 18)
                    .If(dmaChannels.Length > 1)
                        .Then(r => r.WithFlag(19, out packetDuplicationControl, name: "MACECR.PDC (PDC)"))
                        .Else(r => r.WithReservedBits(19, 1))
                    .WithReservedBits(20, 4)
                    .WithTaggedFlag("MACECR.EIPGEN (EIPGEN)", 24)
                    .WithTag("MACECR.EIPG (EIPG)", 25, 5)
                    .WithReservedBits(30, 2)
                },
                {(long)RegistersMacAndMmc.PacketFilteringControl, new DoubleWordRegister(this)
                    .WithTaggedFlag("MACPFR.PR (PR)", 0)
                    .WithTaggedFlag("MACPFR.HUC (HUC)", 1)
                    .WithTaggedFlag("MACPFR.HMC (HMC)", 2)
                    .WithTaggedFlag("MACPFR.DAIF (DAIF)", 3)
                    .WithTaggedFlag("MACPFR.PM (PM)", 4)
                    .WithTaggedFlag("MACPFR.DBF (DBF)", 5)
                    .WithTag("MACPFR.PCF (PCF)", 6, 2)
                    .WithTaggedFlag("MACPFR.SAIF (SAIF)", 8)
                    .WithTaggedFlag("MACPFR.SAF (SAF)", 9)
                    .WithTaggedFlag("MACPFR.HPF (HPF)", 10)
                    .WithReservedBits(11, 5)
                    .WithTaggedFlag("MACPFR.VTFE (VTFE)", 16)
                    .WithReservedBits(17, 3)
                    .WithTaggedFlag("MACPFR.IPFE (IPFE)", 20)
                    .WithTaggedFlag("MACPFR.DNTU (DNTU)", 21)
                    .WithReservedBits(22, 9)
                    .WithTaggedFlag("MACPFR.RA (RA)", 31)
                },
                {(long)RegistersMacAndMmc.WatchdogTimeout, new DoubleWordRegister(this)
                    .WithTag("MACWTR.WTO (WTO)", 0, 4)
                    .WithReservedBits(4, 4)
                    .WithTaggedFlag("MACWTR.PWE (PWE)", 8)
                    .WithReservedBits(9, 23)
                },
                {(long)RegistersMacAndMmc.HashTable0, new DoubleWordRegister(this)
                    .WithTag("MACHT0R.HT31T0 (HT31T0)", 0, 32)
                },
                {(long)RegistersMacAndMmc.HashTable1, new DoubleWordRegister(this)
                    .WithTag("MACHT1R.HT63T32 (HT63T32)", 0, 32)
                },
                {(long)RegistersMacAndMmc.VLANTag, new DoubleWordRegister(this)
                    .WithTag("MACVTR.VL (VL)", 0, 16)
                    .WithTaggedFlag("MACVTR.ETV (ETV)", 16)
                    .WithTaggedFlag("MACVTR.VTIM (VTIM)", 17)
                    .WithTaggedFlag("MACVTR.ESVL (ESVL)", 18)
                    .WithTaggedFlag("MACVTR.ERSVLM (ERSVLM)", 19)
                    .WithTaggedFlag("MACVTR.DOVLTC (DOVLTC)", 20)
                    .WithTag("MACVTR.EVLS (EVLS)", 21, 2)
                    .WithReservedBits(23, 1)
                    .WithTaggedFlag("MACVTR.EVLRXS (EVLRXS)", 24)
                    .WithTaggedFlag("MACVTR.VTHM (VTHM)", 25)
                    .WithTaggedFlag("MACVTR.EDVLP (EDVLP)", 26)
                    .WithTaggedFlag("MACVTR.ERIVLT (ERIVLT)", 27)
                    .WithTag("MACVTR.EIVLS (EIVLS)", 28, 2)
                    .WithReservedBits(30, 1)
                    .WithTaggedFlag("MACVTR.EIVLRXS (EIVLRXS)", 31)
                },
                {(long)RegistersMacAndMmc.VLANHashTable, new DoubleWordRegister(this)
                    .WithTag("MACVHTR.VLHT (VLHT)", 0, 16)
                    .WithReservedBits(16, 16)
                },
                {(long)RegistersMacAndMmc.VLANInclusion, new DoubleWordRegister(this)
                    .WithTag("MACVIR.VLT (VLT)", 0, 16)
                    .WithTag("MACVIR.VLC (VLC)", 16, 2)
                    .WithTaggedFlag("MACVIR.VLP (VLP)", 18)
                    .WithTaggedFlag("MACVIR.CSVL (CSVL)", 19)
                    .WithTaggedFlag("MACVIR.VLTI (VLTI)", 20)
                    .WithReservedBits(21, 11)
                },
                {(long)RegistersMacAndMmc.InnerVLANInclusion, new DoubleWordRegister(this)
                    .WithTag("MACIVIR.VLT (VLT)", 0, 16)
                    .WithTag("MACIVIR.VLC (VLC)", 16, 2)
                    .WithTaggedFlag("MACIVIR.VLP (VLP)", 18)
                    .WithTaggedFlag("MACIVIR.CSVL (CSVL)", 19)
                    .WithTaggedFlag("MACIVIR.VLTI (VLTI)", 20)
                    .WithReservedBits(21, 11)
                },
                {(long)RegistersMacAndMmc.TxQueueFlowControl, new DoubleWordRegister(this)
                    .WithTaggedFlag("MACQTxFCR.FCB_BPA (FCB_BPA)", 0)
                    .WithTaggedFlag("MACQTxFCR.TFE (TFE)", 1)
                    .WithReservedBits(2, 2)
                    .WithTag("MACQTxFCR.PLT (PLT)", 4, 3)
                    .WithTaggedFlag("MACQTxFCR.DZPQ (DZPQ)", 7)
                    .WithReservedBits(8, 8)
                    .WithTag("MACQTxFCR.PT (PT)", 16, 16)
                },
                {(long)RegistersMacAndMmc.RxFlowControl, new DoubleWordRegister(this)
                    .WithTaggedFlag("MACRxFCR.RFE (RFE)", 0)
                    .WithTaggedFlag("MACRxFCR.UP (UP)", 1)
                    .WithReservedBits(2, 30)
                },
                {(long)RegistersMacAndMmc.InterruptStatus, new DoubleWordRegister(this)
                    .WithReservedBits(0, 3)
                    .WithTaggedFlag("MACISR.PHYIS (PHYIS)", 3)
                    .WithFlag(4, out ptpMessageTypeInterrupt, FieldMode.Read, name: "MACISR.PMTIS (PMTIS)")
                    .WithFlag(5, out lowPowerIdleInterrupt, FieldMode.Read, name: "MACISR.LPIIS (LPIIS)")
                    .WithReservedBits(6, 2)
                    .WithTaggedFlag("MACISR.MMCIS (MMCIS)", 8)
                    .WithFlag(9, FieldMode.Read, valueProviderCallback: _ => MMCRxInterruptStatus, name: "MACISR.MMCRXIS (MMCRXIS)")
                    .WithFlag(10, FieldMode.Read, valueProviderCallback: _ => MMCTxInterruptStatus, name: "MACISR.MMCTXIS (MMCTXIS)")
                    .WithReservedBits(11, 1)
                    .WithFlag(12, out timestampInterrupt, FieldMode.ReadToClear, name: "MACISR.TSIS (TSIS)")
                    .WithTaggedFlag("MACISR.TXSTSIS (TXSTSIS)", 13)
                    .WithTaggedFlag("MACISR.RXSTSIS (RXSTSIS)", 14)
                    .WithReservedBits(15, 17)
                    .WithChangeCallback((_, __) => UpdateInterrupts())
                },
                {(long)RegistersMacAndMmc.InterruptEnable, new DoubleWordRegister(this)
                    .WithReservedBits(0, 3)
                    .WithTaggedFlag("MACIER.PHYIE (PHYIE)", 3)
                    .WithFlag(4, out ptpMessageTypeInterruptEnable, name: "MACIER.PMTIE (PMTIE)")
                    .WithFlag(5, out lowPowerIdleInterruptEnable, name: "MACIER.LPIIE (LPIIE)")
                    .WithReservedBits(6, 6)
                    .WithFlag(12, out timestampInterruptEnable, name: "MACIER.TSIE (TSIE)")
                    .WithTaggedFlag("MACIER.TXSTSIE (TXSTSIE)", 13)
                    .WithTaggedFlag("MACIER.RXSTSIE (RXSTSIE)", 14)
                    .WithReservedBits(15, 17)
                    .WithChangeCallback((_, __) => UpdateInterrupts())
                },
                {(long)RegistersMacAndMmc.RxTxStatus, new DoubleWordRegister(this)
                    .WithTaggedFlag("MACRxTxSR.TJT (TJT)", 0)
                    .WithTaggedFlag("MACRxTxSR.NCARR (NCARR)", 1)
                    .WithTaggedFlag("MACRxTxSR.LCARR (LCARR)", 2)
                    .WithTaggedFlag("MACRxTxSR.EXDEF (EXDEF)", 3)
                    .WithTaggedFlag("MACRxTxSR.LCOL (LCOL)", 4)
                    .WithTaggedFlag("MACRxTxSR.EXCOL (LCOL)", 5)
                    .WithReservedBits(6, 2)
                    .WithTaggedFlag("MACRxTxSR.RWT (RWT)", 8)
                    .WithReservedBits(9, 23)
                },
                {(long)RegistersMacAndMmc.PMTControlStatus, new DoubleWordRegister(this)
                    .WithTaggedFlag("MACPCSR.PWRDWN (PWRDWN)", 0)
                    .WithTaggedFlag("MACPCSR.MGKPKTEN (MGKPKTEN)", 1)
                    .WithTaggedFlag("MACPCSR.RWKPKTEN (RWKPKTEN)", 2)
                    .WithReservedBits(3, 2)
                    .WithTaggedFlag("MACPCSR.MGKPRCVD (MGKPRCVD)", 5)
                    .WithTaggedFlag("MACPCSR.RWKPRCVD (RWKPRCVD)", 6)
                    .WithReservedBits(7, 2)
                    .WithTaggedFlag("MACPCSR.GLBLUCAST (GLBLUCAST)", 9)
                    .WithTaggedFlag("MACPCSR.RWKPFE (RWKPFE)", 10)
                    .WithReservedBits(11, 13)
                    .WithTag("MACPCSR.RWKPTR (RWKPTR)", 24, 5)
                    .WithReservedBits(29, 2)
                    .WithTaggedFlag("MACPCSR.RWKFILTRST (RWKFILTRST)", 31)
                },
                {(long)RegistersMacAndMmc.RemoteWakeUpPacketFilter, new DoubleWordRegister(this)
                    .WithTag("MACRWKPFR.MACRWKPFR (MACRWKPFR)", 0, 32)
                },
                {(long)RegistersMacAndMmc.LPIControlAndStatus, new DoubleWordRegister(this)
                    .WithTaggedFlag("MACLCSR.TLPIEN (TLPIEN)", 0)
                    .WithTaggedFlag("MACLCSR.TLPIEX (TLPIEX)", 1)
                    .WithTaggedFlag("MACLCSR.RLPIEN (RLPIEN)", 2)
                    .WithTaggedFlag("MACLCSR.RLPIEX (RLPIEX)", 3)
                    .WithReservedBits(4, 4)
                    .WithTaggedFlag("MACLCSR.TLPIST (TLPIST)", 8)
                    .WithTaggedFlag("MACLCSR.RLPIST (RLPIST)", 9)
                    .WithReservedBits(10, 6)
                    .WithTaggedFlag("MACLCSR.LPIEN (LPIEN)", 16)
                    .WithTaggedFlag("MACLCSR.PLS (PLS)", 17)
                    .WithTaggedFlag("MACLCSR.PLSEN (PLSEN)", 18)
                    .WithTaggedFlag("MACLCSR.LPITXA (LPITXA)", 19)
                    .WithTaggedFlag("MACLCSR.LPITE (LPITE)", 20)
                    .WithReservedBits(21, 11)
                },
                {(long)RegistersMacAndMmc.LPITimersControl, new DoubleWordRegister(this, 0x3E80000)
                    .WithTag("MACLTCR.TWT (TWT)", 0, 16)
                    .WithTag("MACLTCR.LST (LST)", 16, 10)
                    .WithReservedBits(26, 6)
                },
                {(long)RegistersMacAndMmc.LPIEntryTimer, new DoubleWordRegister(this)
                    .WithTag("MACLETR.LPIET (LPIET)", 0, 17)
                    .WithReservedBits(17, 15)
                },
                {(long)RegistersMacAndMmc.OneMicrosecondTickCounter, new DoubleWordRegister(this)
                    .WithTag("MAC1USTCR.TIC_1US_CNTR (TIC_1US_CNTR)", 0, 12)
                    .WithReservedBits(12, 20)
                },
                {(long)RegistersMacAndMmc.Version, new DoubleWordRegister(this)
                    .WithValueField(0, 8, FieldMode.Read, name: "MACVR.SNPSVER (SNPSVER)",
                        valueProviderCallback: _ => IPVersion)
                    .WithValueField(8, 8, FieldMode.Read, name: "MACVR.USERVER (USERVER)",
                        valueProviderCallback: _ => UserIPVersion)
                    .WithReservedBits(16, 16)
                },
                {(long)RegistersMacAndMmc.Debug, new DoubleWordRegister(this)
                    .WithTaggedFlag("MACDR.RPESTS (RPESTS)", 0)
                    .WithTag("MACDR.RFCFCSTS (RFCFCSTS)", 1, 2)
                    .WithReservedBits(3, 13)
                    .WithTaggedFlag("MACDR.TPESTS (TPESTS)", 16)
                    .WithTag("MACDR.TFCSTS (TFCSTS)", 17, 2)
                    .WithReservedBits(19, 13)
                },
                {(long)RegistersMacAndMmc.HardwareFeature0, new DoubleWordRegister(this, 0x0A0D_73F7)
                    .WithTaggedFlag("MIISEL", 0)
                    .WithTaggedFlag("GMIISEL", 1)
                    .WithTaggedFlag("HDSEL", 2)
                    .WithTaggedFlag("PCSSEL", 3)
                    .WithTaggedFlag("VLHASH", 4)
                    .WithTaggedFlag("SMASEL", 5)
                    .WithTaggedFlag("RWKSEL", 6)
                    .WithTaggedFlag("MGKSEL", 7)
                    .WithTaggedFlag("MMCSEL", 8)
                    .WithTaggedFlag("ARPOFFSEL", 9)
                    .WithReservedBits(10, 2)
                    .WithTaggedFlag("TSSEL", 12)
                    .WithTaggedFlag("EEESEL", 13)
                    .WithTaggedFlag("TXCOESEL", 14)
                    .WithReservedBits(15, 1)
                    .WithTaggedFlag("RXCOESEL", 16)
                    .WithReservedBits(17, 1)
                    .WithTag("ADDMACADRSEL", 18, 5)
                    .WithTaggedFlag("MACADR32SEL", 23)
                    .WithTaggedFlag("MACADR64SEL", 24)
                    .WithTag("TSSTSSEL", 25, 2)
                    .WithTaggedFlag("SAVLANINS", 27)
                    .WithEnumField<DoubleWordRegister, PhyInterface>(28, 3, FieldMode.Read, name: "ACTPHYSEL",
                        valueProviderCallback: _ => ActivePhy)
                    .WithReservedBits(31, 1)
                },
                {(long)RegistersMacAndMmc.HardwareFeature1, new DoubleWordRegister(this, 0x1104_1904)
                    .WithValueField(0, 5, FieldMode.Read, name: "MACHWF1R.RXFIFOSIZE (RXFIFOSIZE)",
                        valueProviderCallback: _ => (byte)(Misc.Logarithm2(RxQueueSize) - 7))
                    .WithReservedBits(5, 1)
                    .WithValueField(6, 5, FieldMode.Read, name: "MACHWF1R.TXFIFOSIZE (TXFIFOSIZE)",
                        valueProviderCallback: _ => (byte)(Misc.Logarithm2(TxQueueSize) - 7))
                    .WithTaggedFlag("MACHWF1R.OSTEN (OSTEN)", 11)
                    .WithTaggedFlag("MACHWF1R.PTOEN (PTOEN)", 12)
                    .WithTaggedFlag("MACHWF1R.ADVTHWORD (ADVTHWORD)", 13)
                    .WithEnumField<DoubleWordRegister, AddressWidth>(14, 2, FieldMode.Read, name: "MACHWF1R.ADDR64 (ADDR64)",
                        valueProviderCallback: _ => Address64)
                    .WithTaggedFlag("MACHWF1R.DCBEN (DCBEN)", 16)
                    .WithTaggedFlag("MACHWF1R.SPHEN (SPHEN)", 17)
                    .WithTaggedFlag("MACHWF1R.TSOEN (TSOEN)", 18)
                    .WithTaggedFlag("MACHWF1R.DBGMEMA (DBGMEMA)", 19)
                    .WithTaggedFlag("MACHWF1R.AVSEL (AVSEL)", 20)
                    .WithReservedBits(21, 3)
                    .WithTag("MACHWF1R.HASHTBLSZ (HASHTBLSZ)", 24, 2)
                    .WithReservedBits(26, 1)
                    .WithTag("MACHWF1R.L3L4FNUM (L3L4FNUM)", 27, 4)
                    .WithReservedBits(31, 1)
                },
                // Currently the model has the same amount of TX and RX DMA channels
                // and 1 MLT queue per each TX/RX DMA channel.
                {(long)RegistersMacAndMmc.HardwareFeature2, new DoubleWordRegister(this, 0x4100_0000)
                    .WithValueField(0, 4, FieldMode.Read, name: "MACHWF2R.RXQCNT (RXQCNT)",
                        valueProviderCallback: _ => (ulong)(dmaChannels.Length - 1))
                    .WithReservedBits(4, 2)
                    .WithValueField(6, 4, FieldMode.Read, name: "MACHWF2R.TXQCNT (TXQCNT)",
                        valueProviderCallback: _ => (ulong)(dmaChannels.Length - 1))
                    .WithReservedBits(10, 2)
                    .WithValueField(12, 4, FieldMode.Read, name: "MACHWF2R.RXCHCNT (RXCHCNT)",
                        valueProviderCallback: _ => (ulong)(dmaChannels.Length - 1))
                    .WithReservedBits(16, 2)
                    .WithValueField(18, 4, FieldMode.Read, name: "MACHWF2R.TXCHCNT (TXCHCNT)",
                        valueProviderCallback: _ => (ulong)(dmaChannels.Length - 1))
                    .WithReservedBits(22, 2)
                    .WithTag("MACHWF2R.PPSOUTNUM (PPSOUTNUM)", 24, 3)
                    .WithReservedBits(27, 1)
                    .WithTag("MACHWF2R.AUXSNAPNUM (AUXSNAPNUM)", 28, 3)
                    .WithReservedBits(31, 1)
                },
                {(long)RegistersMacAndMmc.HardwareFeature3, new DoubleWordRegister(this, 0x20)
                    .WithTag("NRVF", 0, 3)
                    .WithReservedBits(3, 1)
                    .WithTaggedFlag("CBTISEL", 4)
                    .WithTaggedFlag("DVLAN", 5)
                    .WithReservedBits(6, 26)
                },
                {(long)RegistersMacAndMmc.MDIOAddress, new DoubleWordRegister(this)
                    .WithFlag(0, out miiBusy, name: "MACMDIOAR.MB (MB)")
                    .WithFlag(1, out clause45PhyEnable, name: "MACMDIOAR.C45E (C45E)")
                    .WithEnumField<DoubleWordRegister, MIIOperation>(2, 2, out miiOperation, name: "MACMDIOAR.GOC (GOC)")
                    .WithTaggedFlag("MACMDIOAR.SKAP (SKAP)", 4)
                    .WithReservedBits(5, 3)
                    .WithValueField(8, 4, changeCallback: (_, value) => { if(value >= 0b0110 && value <= 0b0111) this.Log(LogLevel.Warning, "Reserved CSR Clock Range (0x{0:X}).", value); }, name: "MACMDIOAR.CR (CR)")
                    .WithTag("MACMDIOAR.NTC (NTC)", 12, 3)
                    .WithReservedBits(15, 1)
                    .WithValueField(16, 5, out miiRegisterOrDeviceAddress, name: "MACMDIOAR.RDA (RDA)")
                    .WithValueField(21, 5, out miiPhy, name: "MACMDIOAR.PA (PA)")
                    .WithTaggedFlag("MACMDIOAR.BTB (BTB)", 26)
                    .WithTaggedFlag("MACMDIOAR.PSE (PSE)", 27)
                    .WithReservedBits(28, 4)
                    .WithWriteCallback((_, __) => ExecuteMIIOperation())
                },
                {(long)RegistersMacAndMmc.MDIOData, new DoubleWordRegister(this)
                    .WithValueField(0, 16, out miiData, name: "MACMDIODR.MD (MD)")
                    .WithValueField(16, 16, out miiAddress, name: "MACMDIODR.RA (RA)")
                },
                {(long)RegistersMacAndMmc.ARPAddress, new DoubleWordRegister(this)
                    .WithTag("MACARPAR.ARPPA (ARPPA)", 0, 32)
                },
                {(long)RegistersMacAndMmc.CSRSoftwareControl, new DoubleWordRegister(this)
                    .WithTaggedFlag("RCW", 0)
                    .WithReservedBits(1, 7)
                    .WithTaggedFlag("SEEN", 8)
                    .WithReservedBits(9, 23)
                },
                {(long)RegistersMacAndMmc.MACAddress0High, new DoubleWordRegister(this, 0x8000FFFF)
                    .WithValueField(0, 8, name: "MACA0HR.ADDRHI (ADDRHI) [MAC.E]",
                        valueProviderCallback: _ => MAC.E,
                        writeCallback: (_, value) => MAC = MAC.WithNewOctets(e: (byte)value))
                    .WithValueField(8, 8, name: "MACA0HR.ADDRHI (ADDRHI) [MAC.F]",
                        valueProviderCallback: _ => MAC.F,
                        writeCallback: (_, value) => MAC = MAC.WithNewOctets(f: (byte)value))
                    .If(dmaChannels.Length > 1)
                        .Then(r => r
                            .WithValueField(16, dmaChannels.Length, out dmaChannelSelect, name: "MACA0HR.DCS (DCS)")
                            .WithReservedBits(16 + dmaChannels.Length, 15 - dmaChannels.Length))
                        .Else(r => r.WithReservedBits(16, 15))
                    .WithFlag(31, FieldMode.Read, name: "MACA0HR.AE (AE)",
                        valueProviderCallback: _ => true)
                },
                {(long)RegistersMacAndMmc.MACAddress0Low, new DoubleWordRegister(this, 0xFFFFFFFF)
                    .WithValueField(0, 8, name: "MACA0LR.ADDRLO (ADDRLO) [MAC.A]",
                        valueProviderCallback: _ => MAC.A,
                        writeCallback: (_, value) => MAC = MAC.WithNewOctets(a: (byte)value))
                    .WithValueField(8, 8, name: "MACA0LR.ADDRLO (ADDRLO) [MAC.B]",
                        valueProviderCallback: _ => MAC.B,
                        writeCallback: (_, value) => MAC = MAC.WithNewOctets(b: (byte)value))
                    .WithValueField(16, 8, name: "MACA0LR.ADDRLO (ADDRLO) [MAC.C]",
                        valueProviderCallback: _ => MAC.C,
                        writeCallback: (_, value) => MAC = MAC.WithNewOctets(c: (byte)value))
                    .WithValueField(24, 8, name: "MACA0LR.ADDRLO (ADDRLO) [MAC.D]",
                        valueProviderCallback: _ => MAC.D,
                        writeCallback: (_, value) => MAC = MAC.WithNewOctets(d: (byte)value))
                },
                {(long)RegistersMacAndMmc.MACAddress1High, new DoubleWordRegister(this, 0xFFFF)
                    .WithValueField(0, 8, name: "MACA1HR.ADDRHI (ADDRHI) [MAC1.E]",
                        valueProviderCallback: _ => MAC1.E,
                        writeCallback: (_, value) => MAC1 = MAC1.WithNewOctets(e: (byte)value))
                    .WithValueField(8, 8, name: "MACA1HR.ADDRHI (ADDRHI) [MAC1.F]",
                        valueProviderCallback: _ => MAC1.F,
                        writeCallback: (_, value) => MAC1 = MAC1.WithNewOctets(f: (byte)value))
                    .WithReservedBits(16, 8)
                    .WithTag("MACA1HR.MBC (MBC)", 24, 6)
                    .WithTaggedFlag("MACA1HR.SA (SA)", 30)
                    .WithTaggedFlag("MACA1HR.AE (AE)", 31)
                },
                {(long)RegistersMacAndMmc.MACAddress1Low, new DoubleWordRegister(this, 0xFFFFFFFF)
                    .WithValueField(0, 8, name: "MACA1LR.ADDRLO (ADDRLO) [MAC.A]",
                        valueProviderCallback: _ => MAC1.A,
                        writeCallback: (_, value) => MAC1 = MAC1.WithNewOctets(a: (byte)value))
                    .WithValueField(8, 8, name: "MACA1LR.ADDRLO (ADDRLO) [MAC1.B]",
                        valueProviderCallback: _ => MAC1.B,
                        writeCallback: (_, value) => MAC = MAC.WithNewOctets(b: (byte)value))
                    .WithValueField(16, 8, name: "MACA1LR.ADDRLO (ADDRLO) [MAC1.C]",
                        valueProviderCallback: _ => MAC1.C,
                        writeCallback: (_, value) => MAC1 = MAC1.WithNewOctets(c: (byte)value))
                    .WithValueField(24, 8, name: "MACA1LR.ADDRLO (ADDRLO) [MAC1.D]",
                        valueProviderCallback: _ => MAC1.D,
                        writeCallback: (_, value) => MAC1 = MAC1.WithNewOctets(d: (byte)value))
                },
                {(long)RegistersMacAndMmc.MACAddress2High, new DoubleWordRegister(this, 0xFFFF)
                    .WithTag("MACA2HR.ADDRHI (ADDRHI)", 0, 16)
                    .WithReservedBits(16, 8)
                    .WithTag("MACA2HR.MBC (MBC)", 24, 6)
                    .WithTaggedFlag("MACA2HR.SA (SA)", 30)
                    .WithTaggedFlag("MACA2HR.AE (AE)", 31)
                },
                {(long)RegistersMacAndMmc.MACAddress2Low, new DoubleWordRegister(this, 0xFFFFFFFF)
                    .WithTag("MACA2LR.ADDRLO (ADDRLO)", 0, 32)
                },
                {(long)RegistersMacAndMmc.MACAddress3High, new DoubleWordRegister(this, 0xFFFF)
                    .WithTag("MACA3HR.ADDRHI (ADDRHI)", 0, 16)
                    .WithReservedBits(16, 8)
                    .WithTag("MACA3HR.MBC (MBC)", 24, 6)
                    .WithTaggedFlag("MACA3HR.SA (SA)", 30)
                    .WithTaggedFlag("MACA3HR.AE (AE)", 31)
                },
                {(long)RegistersMacAndMmc.MACAddress3Low, new DoubleWordRegister(this, 0xFFFFFFFF)
                    .WithTag("MACA3LR.ADDRLO (ADDRLO)", 0, 32)
                },
                {(long)RegistersMacAndMmc.MMCControl, new DoubleWordRegister(this)
                    .WithTaggedFlag("MMC_CONTROL.CNTRST (CNTRST)", 0)
                    .WithTaggedFlag("MMC_CONTROL.CNTSTOPRO (CNTSTOPRO)", 1)
                    .WithTaggedFlag("MMC_CONTROL.RSTONRD (RSTONRD)", 2)
                    .WithTaggedFlag("MMC_CONTROL.CNTFREEZ (CNTFREEZ)", 3)
                    .WithTaggedFlag("MMC_CONTROL.CNTPRST (CNTPRST)", 4)
                    .WithTaggedFlag("MMC_CONTROL.CNTPRSTLVL (CNTPRSTLVL)", 5)
                    .WithReservedBits(6, 2)
                    .WithTaggedFlag("MMC_CONTROL.UCDBC (UCDBC)", 8)
                    .WithReservedBits(9, 23)
                },
                {(long)RegistersMacAndMmc.MMCRxInterrupt, new DoubleWordRegister(this)
                    .WithFlag(0, out rxPacketCounterInterrupt, FieldMode.ReadToClear, name: "MMC_RX_INTERRUPT.RXGBPKTIS (MMC Receive Good Bad Packet Counter Interrupt Status)")
                    .WithFlag(1, out rxByteCounterInterrupt, FieldMode.ReadToClear, name: "MMC_RX_INTERRUPT.RXGBOCTIS (MMC Receive Good Bad Octet Counter Interrupt Status)")
                    .WithFlag(2, out rxGoodByteCounterInterrupt, FieldMode.ReadToClear, name: "MMC_RX_INTERRUPT.RXGOCTIS (MMC Receive Good Octet Counter Interrupt Status)")
                    .WithFlag(3, out rxBroadcastPacketCounterInterrupt, FieldMode.ReadToClear, name: "MMC_RX_INTERRUPT.RXBCGPIS (MMC Receive Broadcast Good Packet Counter Interrupt Status)")
                    .WithFlag(4, out rxMulticastPacketCounterInterrupt, FieldMode.ReadToClear, name: "MMC_RX_INTERRUPT.RXMCGPIS (MMC Receive Multicast Good Packet Counter Interrupt Status)")
                    .WithFlag(5, out rxCrcErrorPacketCounterInterrupt, FieldMode.ReadToClear, name: "MMC_RX_INTERRUPT.RXCRCERPIS (MMC Receive CRC Error Packet Counter Interrupt Status)")
                    .WithTaggedFlag("MMC_RX_INTERRUPT.RXALGNERPIS (MMC Receive Alignment Error Packet Counter Interrupt Status)", 6)
                    .WithTaggedFlag("MMC_RX_INTERRUPT.RXRUNTPIS (MMC Receive Runt Packet Counter Interrupt Status)", 7)
                    .WithTaggedFlag("MMC_RX_INTERRUPT.RXJABERPIS (MMC Receive Jabber Error Packet Counter Interrupt Status)", 8)
                    .WithTaggedFlag("MMC_RX_INTERRUPT.RXUSIZEGPIS (MMC Receive Undersize Good Packet Counter Interrupt Status)", 9)
                    .WithTaggedFlag("MMC_RX_INTERRUPT.RXOSIZEGPIS (MMC Receive Oversize Good Packet Counter Interrupt Status)", 10)
                    .WithTaggedFlag("MMC_RX_INTERRUPT.RX64OCTGBPIS (MMC Receive 64 Octet Good Bad Packet Counter Interrupt Status)", 11)
                    .WithTaggedFlag("MMC_RX_INTERRUPT.RX65T127OCTGBPIS (MMC Receive Broadcast Good Bad Packet Counter Interrupt Status)", 12)
                    .WithTaggedFlag("MMC_RX_INTERRUPT.RX128T255OCTGBPIS (MMC Receive 128 to 255 Octet Good Bad Packet Counter Interrupt Status)", 13)
                    .WithTaggedFlag("MMC_RX_INTERRUPT.RX256T511OCTGBPIS (MMC Receive 256 to 511 Octet Good Bad Packet Counter Interrupt Status)", 14)
                    .WithTaggedFlag("MMC_RX_INTERRUPT.RX512T1023OCTGBPIS (MMC Receive 512 to 1023 Octet Good Bad Packet Counter Interrupt Status)", 15)
                    .WithTaggedFlag("MMC_RX_INTERRUPT.RX1024TMAXOCTGBPIS (MMC Receive 1024 to Maximum Octet Good Bad Packet Counter Interrupt Status)", 16)
                    .WithFlag(17, out rxUnicastPacketCounterInterrupt, FieldMode.ReadToClear, name: "MMC_RX_INTERRUPT.RXUCGPIS (MMC Receive Unicast Good Packet Counter Interrupt Status)")
                    .WithTaggedFlag("MMC_RX_INTERRUPT.RXLENERPIS (MMC Receive Length Error Packet Counter Interrupt Status)", 18)
                    .WithTaggedFlag("MMC_RX_INTERRUPT.RXORANGEPIS (MMC Receive Out Of Range Error Packet Counter Interrupt Status)", 19)
                    .WithTaggedFlag("MMC_RX_INTERRUPT.RXPAUSPIS (MMC Receive Pause Packet Counter Interrupt Status)", 20)
                    .WithFlag(21, out rxFifoPacketCounterInterrupt, FieldMode.ReadToClear, name: "MMC_RX_INTERRUPT.RXFOVPIS (MMC Receive FIFO Overflow Packet Counter Interrupt Status)")
                    .WithTaggedFlag("MMC_RX_INTERRUPT.RXVLANGBPIS (MMC Receive VLAN Good Bad Packet Counter Interrupt Status)", 22)
                    .WithTaggedFlag("MMC_RX_INTERRUPT.RXWDOGPIS (MMC Receive Watchdog Error Packet Counter Interrupt Status)", 23)
                    .WithTaggedFlag("MMC_RX_INTERRUPT.RXRCVERRPIS (MMC Receive Error Packet Counter Interrupt Status)", 24)
                    .WithTaggedFlag("MMC_RX_INTERRUPT.RXCTRLPIS (MMC Receive Control Packet Counter Interrupt Status)", 25)
                    .WithTaggedFlag("MMC_RX_INTERRUPT.RXLPIUSCIS (MMC Receive LPI Microsecond Counter Interrupt Status)", 26)
                    .WithTaggedFlag("MMC_RX_INTERRUPT.RXLPITRCIS (MMC Receive LPI Transition Counter Interrupt Status)", 27)
                    .WithReservedBits(28, 4)
                    .WithChangeCallback((_, __) => UpdateInterrupts())
                },
                {(long)RegistersMacAndMmc.MMCTxInterrupt, new DoubleWordRegister(this)
                    .WithFlag(0, out txByteCounterInterrupt, FieldMode.ReadToClear, name: "MMC_TX_INTERRUPT.TXGBOCTIS (MMC Transmit Good Bad Octet Counter Interrupt Status)")
                    .WithFlag(1, out txPacketCounterInterrupt, FieldMode.ReadToClear, name: "MMC_TX_INTERRUPT.TXGBPKTIS (MMC Transmit Good Bad Packet Counter Interrupt Status)")
                    .WithTaggedFlag("MMC_TX_INTERRUPT.TXBCGPIS (MMC Transmit Broadcast Good Packet Counter Interrupt Status)", 2)
                    .WithTaggedFlag("MMC_TX_INTERRUPT.TXMCGPIS (MMC Transmit Multicast Good Packet Counter Interrupt Status)", 3)
                    .WithTaggedFlag("MMC_TX_INTERRUPT.TX64OCTGBPIS (MMC Transmit 64 Octet Good Bad Packet Counter Interrupt Status)", 4)
                    .WithTaggedFlag("MMC_TX_INTERRUPT.TX65T127OCTGBPIS (MMC Transmit 65 to 127 Octet Good Bad Packet Counter Interrupt Status)", 5)
                    .WithTaggedFlag("MMC_TX_INTERRUPT.TX128T255OCTGBPIS (MMC Transmit 128 to 255 Octet Good Bad Packet Counter Interrupt Status)", 6)
                    .WithTaggedFlag("MMC_TX_INTERRUPT.TX256T511OCTGBPIS (MMC Transmit 256 to 511 Octet Good Bad Packet Counter Interrupt Status)", 7)
                    .WithTaggedFlag("MMC_TX_INTERRUPT.TX512T1023OCTGBPIS (MMC Transmit 512 to 1023 Octet Good Bad Packet Counter Interrupt Status)", 8)
                    .WithTaggedFlag("MMC_TX_INTERRUPT.TX1024TMAXOCTGBPIS (MMC Transmit 1024 to Maximum Octet Good Bad Packet Counter Interrupt Status)", 9)
                    .WithFlag(10, out txUnicastPacketCounterInterrupt, FieldMode.ReadToClear, name: "MMC_TX_INTERRUPT.TXUCGBPIS (MMC Transmit Unicast Good Bad Packet Counter Interrupt Status)")
                    .WithFlag(11, out txMulticastPacketCounterInterrupt, FieldMode.ReadToClear, name: "MMC_TX_INTERRUPT.TXMCGBPIS (MMC Transmit Multicast Good Bad Packet Counter Interrupt Status)")
                    .WithFlag(12, out txBroadcastPacketCounterInterrupt, FieldMode.ReadToClear, name: "MMC_TX_INTERRUPT.TXBCGBPIS (MMC Transmit Broadcast Good Bad Packet Counter Interrupt Status)")
                    .WithTaggedFlag("MMC_TX_INTERRUPT.TXUFLOWERPIS (MMC Transmit Underflow Error Packet Counter Interrupt Status)", 13)
                    .WithTaggedFlag("MMC_TX_INTERRUPT.TXSCOLGPIS (MMC Transmit Single Collision Good Packet Counter Interrupt Status)", 14)
                    .WithTaggedFlag("MMC_TX_INTERRUPT.TXMCOLGPIS (MMC Transmit Multiple Collision Good Packet Counter Interrupt Status)", 15)
                    .WithTaggedFlag("MMC_TX_INTERRUPT.TXDEFPIS (MMC Transmit Deferred Packet Counter Interrupt Status)", 16)
                    .WithTaggedFlag("MMC_TX_INTERRUPT.TXLATCOLPIS (MMC Transmit Late Collision Packet Counter Interrupt Status)", 17)
                    .WithTaggedFlag("MMC_TX_INTERRUPT.TXEXCOLPIS (MMC Transmit Excessive Collision Packet Counter Interrupt Status)", 18)
                    .WithTaggedFlag("MMC_TX_INTERRUPT.TXCARERPIS (MMC Transmit Carrier Error Packet Counter Interrupt Status)", 19)
                    .WithFlag(20, out txGoodByteCounterInterrupt, FieldMode.ReadToClear, name: "MMC_TX_INTERRUPT.TXGOCTIS (MMC Transmit Good Octet Counter Interrupt Status)")
                    .WithFlag(21, out txGoodPacketCounterInterrupt, FieldMode.ReadToClear, name: "MMC_TX_INTERRUPT.TXGPKTIS (MMC Transmit Good Packet Counter Interrupt Status)")
                    .WithTaggedFlag("MMC_TX_INTERRUPT.TXEXDEFPIS (MMC Transmit Excessive Deferral Packet Counter Interrupt Status)", 22)
                    .WithTaggedFlag("MMC_TX_INTERRUPT.TXPAUSPIS (MMC Transmit Pause Packet Counter Interrupt Status)", 23)
                    .WithTaggedFlag("MMC_TX_INTERRUPT.TXVLANGPIS (MMC Transmit VLAN Good Packet Counter Interrupt Status)", 24)
                    .WithTaggedFlag("MMC_TX_INTERRUPT.TXOSIZEGPIS (MMC Transmit Oversize Good Packet Counter Interrupt Status)", 25)
                    .WithTaggedFlag("MMC_TX_INTERRUPT.TXLPIUSCIS (MMC Transmit LPI Microsecond Counter Interrupt Status)", 26)
                    .WithTaggedFlag("MMC_TX_INTERRUPT.TXLPITRCIS (MMC Transmit LPI Transition Counter Interrupt Status)", 27)
                    .WithReservedBits(28, 4)
                    .WithChangeCallback((_, __) => UpdateInterrupts())
                },
                {(long)RegistersMacAndMmc.MMCRxInterruptMask, new DoubleWordRegister(this)
                    .WithFlag(0, out rxPacketCounterInterruptEnable, name: "MMC_RX_INTERRUPT_MASK.RXGBPKTIM (MMC Receive Good Bad Packet Counter Interrupt Mask)")
                    .WithFlag(1, out rxByteCounterInterruptEnable, name: "MMC_RX_INTERRUPT_MASK.RXGBOCTIM (MMC Receive Good Bad Octet Counter Interrupt Mask)")
                    .WithFlag(2, out rxGoodByteCounterInterruptEnable, name: "MMC_RX_INTERRUPT_MASK.RXGOCTIM (MMC Receive Good Octet Counter Interrupt Mask)")
                    .WithFlag(3, out rxBroadcastPacketCounterInterruptEnable, name: "MMC_RX_INTERRUPT_MASK.RXBCGPIM (MMC Receive Broadcast Good Packet Counter Interrupt Mask)")
                    .WithFlag(4, out rxMulticastPacketCounterInterruptEnable, name: "MMC_RX_INTERRUPT_MASK.RXMCGPIM (MMC Receive Multicast Good Packet Counter Interrupt Mask)")
                    .WithFlag(5, out rxCrcErrorPacketCounterInterruptEnable, name: "MMC_RX_INTERRUPT_MASK.RXCRCERPIM (MMC Receive CRC Error Packet Counter Interrupt Mask)")
                    .WithTaggedFlag("MMC_RX_INTERRUPT_MASK.RXALGNERPIM (MMC Receive Alignment Error Packet Counter Interrupt Mask)", 6)
                    .WithTaggedFlag("MMC_RX_INTERRUPT_MASK.RXRUNTPIM (MMC Receive Runt Packet Counter Interrupt Mask)", 7)
                    .WithTaggedFlag("MMC_RX_INTERRUPT_MASK.RXJABERPIM (MMC Receive Jabber Error Packet Counter Interrupt Mask)", 8)
                    .WithTaggedFlag("MMC_RX_INTERRUPT_MASK.RXUSIZEGPIM (MMC Receive Undersize Good Packet Counter Interrupt Mask)", 9)
                    .WithTaggedFlag("MMC_RX_INTERRUPT_MASK.RXOSIZEGPIM (MMC Receive Oversize Good Packet Counter Interrupt Mask)", 10)
                    .WithTaggedFlag("MMC_RX_INTERRUPT_MASK.RX64OCTGBPIM (MMC Receive 64 Octet Good Bad Packet Counter Interrupt Mask)", 11)
                    .WithTaggedFlag("MMC_RX_INTERRUPT_MASK.RX65T127OCTGBPIM (MMC Receive Broadcast Good Bad Packet Counter Interrupt Mask)", 12)
                    .WithTaggedFlag("MMC_RX_INTERRUPT_MASK.RX128T255OCTGBPIM (MMC Receive 128 to 255 Octet Good Bad Packet Counter Interrupt Mask)", 13)
                    .WithTaggedFlag("MMC_RX_INTERRUPT_MASK.RX256T511OCTGBPIM (MMC Receive 256 to 511 Octet Good Bad Packet Counter Interrupt Mask)", 14)
                    .WithTaggedFlag("MMC_RX_INTERRUPT_MASK.RX512T1023OCTGBPIM (MMC Receive 512 to 1023 Octet Good Bad Packet Counter Interrupt Mask)", 15)
                    .WithTaggedFlag("MMC_RX_INTERRUPT_MASK.RX1024TMAXOCTGBPIM (MMC Receive 1024 to Maximum Octet Good Bad Packet Counter Interrupt Mask)", 16)
                    .WithFlag(17, out rxUnicastPacketCounterInterruptEnable, name: "MMC_RX_INTERRUPT_MASK.RXUCGPIM (MMC Receive Unicast Good Packet Counter Interrupt Mask)")
                    .WithTaggedFlag("MMC_RX_INTERRUPT_MASK.RXLENERPIM (MMC Receive Length Error Packet Counter Interrupt Mask)", 18)
                    .WithTaggedFlag("MMC_RX_INTERRUPT_MASK.RXORANGEPIM (MMC Receive Out Of Range Error Packet Counter Interrupt Mask)", 19)
                    .WithTaggedFlag("MMC_RX_INTERRUPT_MASK.RXPAUSPIM (MMC Receive Pause Packet Counter Interrupt Mask)", 20)
                    .WithFlag(21, out rxFifoPacketCounterInterruptEnable, name: "MMC_RX_INTERRUPT_MASK.RXFOVPIM (MMC Receive FIFO Overflow Packet Counter Interrupt Mask)")
                    .WithTaggedFlag("MMC_RX_INTERRUPT_MASK.RXVLANGBPIM (MMC Receive VLAN Good Bad Packet Counter Interrupt Mask)", 22)
                    .WithTaggedFlag("MMC_RX_INTERRUPT_MASK.RXWDOGPIM (MMC Receive Watchdog Error Packet Counter Interrupt Mask)", 23)
                    .WithTaggedFlag("MMC_RX_INTERRUPT_MASK.RXRCVERRPIM (MMC Receive Error Packet Counter Interrupt Mask)", 24)
                    .WithTaggedFlag("MMC_RX_INTERRUPT_MASK.RXCTRLPIM (MMC Receive Control Packet Counter Interrupt Mask)", 25)
                    .WithTaggedFlag("MMC_RX_INTERRUPT_MASK.RXLPIUSCIM (MMC Receive LPI Microsecond Counter Interrupt Mask)", 26)
                    .WithTaggedFlag("MMC_RX_INTERRUPT_MASK.RXLPITRCIM (MMC Receive LPI Transition Counter Interrupt Mask)", 27)
                    .WithReservedBits(28, 4)
                    .WithChangeCallback((_, __) => UpdateInterrupts())
                },
                {(long)RegistersMacAndMmc.MMCTxInterruptMask, new DoubleWordRegister(this)
                    .WithFlag(0, out txByteCounterInterruptEnable, name: "MMC_TX_INTERRUPT_MASK.TXGBOCTIM (MMC Transmit Good Bad Octet Counter Interrupt Mask)")
                    .WithFlag(1, out txPacketCounterInterruptEnable, name: "MMC_TX_INTERRUPT_MASK.TXGBPKTIM (MMC Transmit Good Bad Packet Counter Interrupt Mask)")
                    .WithTaggedFlag("MMC_TX_INTERRUPT_MASK.TXBCGPIM (MMC Transmit Broadcast Good Packet Counter Interrupt Mask)", 2)
                    .WithTaggedFlag("MMC_TX_INTERRUPT_MASK.TXMCGPIM (MMC Transmit Multicast Good Packet Counter Interrupt Mask)", 3)
                    .WithTaggedFlag("MMC_TX_INTERRUPT_MASK.TX64OCTGBPIM (MMC Transmit 64 Octet Good Bad Packet Counter Interrupt Mask)", 4)
                    .WithTaggedFlag("MMC_TX_INTERRUPT_MASK.TX65T127OCTGBPIM (MMC Transmit 65 to 127 Octet Good Bad Packet Counter Interrupt Mask)", 5)
                    .WithTaggedFlag("MMC_TX_INTERRUPT_MASK.TX128T255OCTGBPIM (MMC Transmit 128 to 255 Octet Good Bad Packet Counter Interrupt Mask)", 6)
                    .WithTaggedFlag("MMC_TX_INTERRUPT_MASK.TX256T511OCTGBPIM (MMC Transmit 256 to 511 Octet Good Bad Packet Counter Interrupt Mask)", 7)
                    .WithTaggedFlag("MMC_TX_INTERRUPT_MASK.TX512T1023OCTGBPIM (MMC Transmit 512 to 1023 Octet Good Bad Packet Counter Interrupt Mask)", 8)
                    .WithTaggedFlag("MMC_TX_INTERRUPT_MASK.TX1024TMAXOCTGBPIM (MMC Transmit 1024 to Maximum Octet Good Bad Packet Counter Interrupt Mask)", 9)
                    .WithFlag(10, out txUnicastPacketCounterInterruptEnable, name: "MMC_TX_INTERRUPT_MASK.TXUCGBPIM (MMC Transmit Unicast Good Bad Packet Counter Interrupt Mask)")
                    .WithFlag(11, out txMulticastPacketCounterInterruptEnable, name: "MMC_TX_INTERRUPT_MASK.TXMCGBPIM (MMC Transmit Multicast Good Bad Packet Counter Interrupt Mask)")
                    .WithFlag(12, out txBroadcastPacketCounterInterruptEnable, name: "MMC_TX_INTERRUPT_MASK.TXBCGBPIM (MMC Transmit Broadcast Good Bad Packet Counter Interrupt Mask)")
                    .WithTaggedFlag("MMC_TX_INTERRUPT_MASK.TXUFLOWERPIM (MMC Transmit Underflow Error Packet Counter Interrupt Mask)", 13)
                    .WithTaggedFlag("MMC_TX_INTERRUPT_MASK.TXSCOLGPIM (MMC Transmit Single Collision Good Packet Counter Interrupt Mask)", 14)
                    .WithTaggedFlag("MMC_TX_INTERRUPT_MASK.TXMCOLGPIM (MMC Transmit Multiple Collision Good Packet Counter Interrupt Mask)", 15)
                    .WithTaggedFlag("MMC_TX_INTERRUPT_MASK.TXDEFPIM (MMC Transmit Deferred Packet Counter Interrupt Mask)", 16)
                    .WithTaggedFlag("MMC_TX_INTERRUPT_MASK.TXLATCOLPIM (MMC Transmit Late Collision Packet Counter Interrupt Mask)", 17)
                    .WithTaggedFlag("MMC_TX_INTERRUPT_MASK.TXEXCOLPIM (MMC Transmit Excessive Collision Packet Counter Interrupt Mask)", 18)
                    .WithTaggedFlag("MMC_TX_INTERRUPT_MASK.TXCARERPIM (MMC Transmit Carrier Error Packet Counter Interrupt Mask)", 19)
                    .WithFlag(20, out txGoodByteCounterInterruptEnable, name: "MMC_TX_INTERRUPT_MASK.TXGOCTIM (MMC Transmit Good Octet Counter Interrupt Mask)")
                    .WithFlag(21, out txGoodPacketCounterInterruptEnable, name: "MMC_TX_INTERRUPT_MASK.TXGPKTIM (MMC Transmit Good Packet Counter Interrupt Mask)")
                    .WithTaggedFlag("MMC_TX_INTERRUPT_MASK.TXEXDEFPIM (MMC Transmit Excessive Deferral Packet Counter Interrupt Mask)", 22)
                    .WithTaggedFlag("MMC_TX_INTERRUPT_MASK.TXPAUSPIM (MMC Transmit Pause Packet Counter Interrupt Mask)", 23)
                    .WithTaggedFlag("MMC_TX_INTERRUPT_MASK.TXVLANGPIM (MMC Transmit VLAN Good Packet Counter Interrupt Mask)", 24)
                    .WithTaggedFlag("MMC_TX_INTERRUPT_MASK.TXOSIZEGPIM (MMC Transmit Oversize Good Packet Counter Interrupt Mask)", 25)
                    .WithTaggedFlag("MMC_TX_INTERRUPT_MASK.TXLPIUSCIM (MMC Transmit LPI Microsecond Counter Interrupt Mask)", 26)
                    .WithTaggedFlag("MMC_TX_INTERRUPT_MASK.TXLPITRCIM (MMC Transmit LPI Transition Counter Interrupt Mask)", 27)
                    .WithReservedBits(28, 4)
                    .WithChangeCallback((_, __) => UpdateInterrupts())
                },
                {(long)RegistersMacAndMmc.TxOctetCountGoodBad, new DoubleWordRegister(this)
                    .WithValueField(0, 32, out txByteCounter, FieldMode.Read, name: "TX_OCTET_COUNT_GOOD_BAD.TXOCTGB (TXOCTGB)")
                },
                {(long)RegistersMacAndMmc.TxPacketCountGoodBad, new DoubleWordRegister(this)
                    .WithValueField(0, 32, out txPacketCounter, FieldMode.Read, name: "TX_PACKET_COUNT_GOOD_BAD.TXPKTGB (TXPKTGB)")
                },
                {(long)RegistersMacAndMmc.TxUnicastPacketsGoodBad, new DoubleWordRegister(this)
                    .WithValueField(0, 32, out txUnicastPacketCounter, FieldMode.Read, name: "TX_UNICAST_PACKETS_GOOD_BAD.TXUCASTGB (TXUCASTGB)")
                },
                {(long)RegistersMacAndMmc.TxMulticastPacketsGoodBad, new DoubleWordRegister(this)
                    .WithValueField(0, 32, out txMulticastPacketCounter, FieldMode.Read, name: "TX_MULTICAST_PACKETS_GOOD_BAD.TXMCASTGB (TXMCASTGB)")
                },
                {(long)RegistersMacAndMmc.TxBroadcastPacketsGoodBad, new DoubleWordRegister(this)
                    .WithValueField(0, 32, out txBroadcastPacketCounter, FieldMode.Read, name: "TX_BROADCAST_PACKETS_GOOD_BAD.TXBCASTGB (TXBCASTGB)")
                },
                {(long)RegistersMacAndMmc.TxSingleCollisionGoodPackets, new DoubleWordRegister(this)
                    // collisions are impossible
                    .WithValueField(0, 32, FieldMode.Read, name: "TX_SINGLE_COLLISION_GOOD_PACKETS.TXSNGLCOLG (TXSNGLCOLG)")
                },
                {(long)RegistersMacAndMmc.TxMultipleCollisionGoodPackets, new DoubleWordRegister(this)
                    // collisions are impossible
                    .WithValueField(0, 32, FieldMode.Read, name: "TX_MULTIPLE_COLLISION_GOOD_PACKETS.TXMULTCOLG (TXMULTCOLG)")
                },
                {(long)RegistersMacAndMmc.TxOctetCountGood, new DoubleWordRegister(this)
                    .WithValueField(0, 32, out txGoodByteCounter, FieldMode.Read, name: "TX_OCTET_COUNT_GOOD.TXOCTG (TXOCTG)")
                },
                {(long)RegistersMacAndMmc.TxPacketCountGood, new DoubleWordRegister(this)
                    .WithValueField(0, 32, out txGoodPacketCounter, FieldMode.Read, name: "TX_PACKET_COUNT_GOOD.TXPKTG (TXPKTG)")
                },
                {(long)RegistersMacAndMmc.RxPacketsCountGoodBad, new DoubleWordRegister(this)
                    .WithValueField(0, 32, out rxPacketCounter, FieldMode.Read, name: "RX_PACKETS_COUNT_GOOD_BAD.RXPKTGB (RXPKTGB)")
                },
                {(long)RegistersMacAndMmc.RxOctetCountGoodBad, new DoubleWordRegister(this)
                    .WithValueField(0, 32, out rxByteCounter, FieldMode.Read, name: "RX_OCTET_COUNT_GOOD_BAD.RXOCTGB (RXOCTGB)")
                },
                {(long)RegistersMacAndMmc.RxOctetCountGood, new DoubleWordRegister(this)
                    .WithValueField(0, 32, out rxGoodByteCounter, FieldMode.Read, name: "RX_OCTET_COUNT_GOOD.RXOCTG (RXOCTG)")
                },
                {(long)RegistersMacAndMmc.RxBroadcastPacketsGood, new DoubleWordRegister(this)
                    .WithValueField(0, 32, out rxBroadcastPacketCounter, FieldMode.Read, name: "RX_BROADCAST_PACKETS_GOOD.RXBCASTG (RXBCASTG)")
                },
                {(long)RegistersMacAndMmc.RxMulticastPacketsGood, new DoubleWordRegister(this)
                    .WithValueField(0, 32, out rxMulticastPacketCounter, FieldMode.Read, name: "RX_MULTICAST_PACKETS_GOOD.RXMCASTG (RXMCASTG)")
                },
                {(long)RegistersMacAndMmc.RxCRCErrorPackets, new DoubleWordRegister(this)
                    .WithValueField(0, 32, out rxCrcErrorPacketCounter, FieldMode.Read, name: "RX_CRC_ERROR_PACKETS.RXCRCERR (RXCRCERR)")
                },
                {(long)RegistersMacAndMmc.RxAlignmentErrorPackets, new DoubleWordRegister(this)
                    .WithTag("RX_ALIGNMENT_ERROR_PACKETS.RXALGNERR (RXALGNERR)", 0, 32)
                },
                {(long)RegistersMacAndMmc.RxUnicastPacketsGood, new DoubleWordRegister(this)
                    .WithValueField(0, 32, out rxUnicastPacketCounter, FieldMode.Read, name: "RX_UNICAST_PACKETS_GOOD.RXUCASTG (RXUCASTG)")
                },
                {(long)RegistersMacAndMmc.RxFifoOverflowPackets, new DoubleWordRegister(this)
                    .WithValueField(0, 32, out rxFifoPacketCounter, name: "Rx_FIFO_Overflow_Packets.RXFIFOOVFL (RXFIFOOVFL)")
                },
                {(long)RegistersMacAndMmc.TxLPIMicrodecondTimer, new DoubleWordRegister(this)
                    .WithTag("TX_LPI_USEC_CNTR.TXLPIUSC (TXLPIUSC)", 0, 32)
                },
                {(long)RegistersMacAndMmc.TxLPITransitionCounter, new DoubleWordRegister(this)
                    .WithTag("TX_LPI_TRAN_CNTR.TXLPITRC (TXLPITRC)", 0, 32)
                },
                {(long)RegistersMacAndMmc.RxLPIMicrosecondCounter, new DoubleWordRegister(this)
                    .WithTag("RX_LPI_USEC_CNTR.RXLPIUSC (RXLPIUSC)", 0, 32)
                },
                {(long)RegistersMacAndMmc.RxLPITransitionCounter, new DoubleWordRegister(this)
                    .WithTag("RX_LPI_TRAN_CNTR.RXLPITRC (RXLPITRC)", 0, 32)
                },
                {(long)RegistersMacAndMmc.MmcIpcRxInterruptMask, new DoubleWordRegister(this)
                    .WithFlag(0, out rxIpcPacketCounterInterruptEnable[(int)IpcCounter.IpV4Good], name: "MMC_IPC_RX_INTERRUPT_MASK.RXIPV4GPIM (RXIPV4GPIM)")
                    .WithFlag(1, out rxIpcPacketCounterInterruptEnable[(int)IpcCounter.IpV4HeaderError], name: "MMC_IPC_RX_INTERRUPT_MASK.RXIPV4HERPIM (RXIPV4HERPIM)")
                    .WithFlag(2, out rxIpcPacketCounterInterruptEnable[(int)IpcCounter.IpV4NoPayload], name: "MMC_IPC_RX_INTERRUPT_MASK.RXIPV4NOPAYPIM (RXIPV4NOPAYPIM)")
                    .WithFlag(3, out rxIpcPacketCounterInterruptEnable[(int)IpcCounter.IpV4Fragmented], name: "MMC_IPC_RX_INTERRUPT_MASK.RXIPV4FRAGPIM (RXIPV4FRAGPIM)")
                    .WithFlag(4, out rxIpcPacketCounterInterruptEnable[(int)IpcCounter.IpV4UDPChecksumDisabled], name: "MMC_IPC_RX_INTERRUPT_MASK.RXIPV4UDSBLPIM (RXIPV4UDSBLPIM)")
                    .WithFlag(5, out rxIpcPacketCounterInterruptEnable[(int)IpcCounter.IpV6Good], name: "MMC_IPC_RX_INTERRUPT_MASK.RXIPV6GPIM (RXIPV6GPIM)")
                    .WithFlag(6, out rxIpcPacketCounterInterruptEnable[(int)IpcCounter.IpV6HeaderError], name: "MMC_IPC_RX_INTERRUPT_MASK.RXIPV6HERPIM (RXIPV6HERPIM)")
                    .WithFlag(7, out rxIpcPacketCounterInterruptEnable[(int)IpcCounter.IpV6NoPayload], name: "MMC_IPC_RX_INTERRUPT_MASK.RXIPV6NOPAYPIM (RXIPV6NOPAYPIM)")
                    .WithFlag(8, out rxIpcPacketCounterInterruptEnable[(int)IpcCounter.UdpGood], name: "MMC_IPC_RX_INTERRUPT_MASK.RXUDPGPIM (RXUDPGPIM)")
                    .WithFlag(9, out rxIpcPacketCounterInterruptEnable[(int)IpcCounter.UdpError], name: "MMC_IPC_RX_INTERRUPT_MASK.RXUDPERPIM (RXUDPERPIM)")
                    .WithFlag(10, out rxIpcPacketCounterInterruptEnable[(int)IpcCounter.TcpGood], name: "MMC_IPC_RX_INTERRUPT_MASK.RXTCPGPIM (RXTCPGPIM)")
                    .WithFlag(11, out rxIpcPacketCounterInterruptEnable[(int)IpcCounter.TcpError], name: "MMC_IPC_RX_INTERRUPT_MASK.RXTCPERPIM (RXTCPERPIM)")
                    .WithFlag(12, out rxIpcPacketCounterInterruptEnable[(int)IpcCounter.IcmpGood], name: "MMC_IPC_RX_INTERRUPT_MASK.RXICMPGPIM (RXICMPGPIM)")
                    .WithFlag(13, out rxIpcPacketCounterInterruptEnable[(int)IpcCounter.IcmpError], name: "MMC_IPC_RX_INTERRUPT_MASK.RXICMPERPIM (RXICMPERPIM)")
                    .WithReservedBits(14, 2)
                    .WithFlag(16, out rxIpcByteCounterInterruptEnable[(int)IpcCounter.IpV4Good], name: "MMC_IPC_RX_INTERRUPT_MASK.RXIPV4GOIM (RXIPV4GOIM)")
                    .WithFlag(17, out rxIpcByteCounterInterruptEnable[(int)IpcCounter.IpV4HeaderError], name: "MMC_IPC_RX_INTERRUPT_MASK.RXIPV4HEROIM (RXIPV4HEROIM)")
                    .WithFlag(18, out rxIpcByteCounterInterruptEnable[(int)IpcCounter.IpV4NoPayload], name: "MMC_IPC_RX_INTERRUPT_MASK.RXIPV4NOPAYOIM (RXIPV4NOPAYOIM)")
                    .WithFlag(19, out rxIpcByteCounterInterruptEnable[(int)IpcCounter.IpV4Fragmented], name: "MMC_IPC_RX_INTERRUPT_MASK.RXIPV4FRAGOIM (RXIPV4FRAGOIM)")
                    .WithFlag(20, out rxIpcByteCounterInterruptEnable[(int)IpcCounter.IpV4UDPChecksumDisabled], name: "MMC_IPC_RX_INTERRUPT_MASK.RXIPV4UDSBLOIM (RXIPV4UDSBLOIM)")
                    .WithFlag(21, out rxIpcByteCounterInterruptEnable[(int)IpcCounter.IpV6Good], name: "MMC_IPC_RX_INTERRUPT_MASK.RXIPV6GOIM (RXIPV6GOIM)")
                    .WithFlag(22, out rxIpcByteCounterInterruptEnable[(int)IpcCounter.IpV6HeaderError], name: "MMC_IPC_RX_INTERRUPT_MASK.RXIPV6HEROIM (RXIPV6HEROIM)")
                    .WithFlag(23, out rxIpcByteCounterInterruptEnable[(int)IpcCounter.IpV6NoPayload], name: "MMC_IPC_RX_INTERRUPT_MASK.RXIPV6NOPAYOIM (RXIPV6NOPAYOIM)")
                    .WithFlag(24, out rxIpcByteCounterInterruptEnable[(int)IpcCounter.UdpGood], name: "MMC_IPC_RX_INTERRUPT_MASK.RXUDPGOIM (RXUDPGOIM)")
                    .WithFlag(25, out rxIpcByteCounterInterruptEnable[(int)IpcCounter.UdpError], name: "MMC_IPC_RX_INTERRUPT_MASK.RXUDPEROIM (RXUDPEROIM)")
                    .WithFlag(26, out rxIpcByteCounterInterruptEnable[(int)IpcCounter.TcpGood], name: "MMC_IPC_RX_INTERRUPT_MASK.RXTCPGOIM (RXTCPGOIM)")
                    .WithFlag(27, out rxIpcByteCounterInterruptEnable[(int)IpcCounter.TcpError], name: "MMC_IPC_RX_INTERRUPT_MASK.RXTCPEROIM (RXTCPEROIM)")
                    .WithFlag(28, out rxIpcByteCounterInterruptEnable[(int)IpcCounter.IcmpGood], name: "MMC_IPC_RX_INTERRUPT_MASK.RXICMPGOIM (RXICMPGOIM)")
                    .WithFlag(29, out rxIpcByteCounterInterruptEnable[(int)IpcCounter.IcmpError], name: "MMC_IPC_RX_INTERRUPT_MASK.RXICMPEROIM (RXICMPEROIM)")
                    .WithReservedBits(30, 2)
                },
                {(long)RegistersMacAndMmc.MmcIpcRxInterrupt, new DoubleWordRegister(this)
                    .WithFlag(0, out rxIpcPacketCounterInterrupt[(int)IpcCounter.IpV4Good], FieldMode.ReadToClear, name: "MMC_IPC_RX_INTERRUPT.RXIPV4GPIS (RXIPV4GPIS)")
                    .WithFlag(1, out rxIpcPacketCounterInterrupt[(int)IpcCounter.IpV4HeaderError], FieldMode.ReadToClear, name: "MMC_IPC_RX_INTERRUPT.RXIPV4HERPIS (RXIPV4HERPIS)")
                    .WithFlag(2, out rxIpcPacketCounterInterrupt[(int)IpcCounter.IpV4NoPayload], FieldMode.ReadToClear, name: "MMC_IPC_RX_INTERRUPT.RXIPV4NOPAYPIS (RXIPV4NOPAYPIS)")
                    .WithFlag(3, out rxIpcPacketCounterInterrupt[(int)IpcCounter.IpV4Fragmented], FieldMode.ReadToClear, name: "MMC_IPC_RX_INTERRUPT.RXIPV4FRAGPIS (RXIPV4FRAGPIS)")
                    .WithFlag(4, out rxIpcPacketCounterInterrupt[(int)IpcCounter.IpV4UDPChecksumDisabled], FieldMode.ReadToClear, name: "MMC_IPC_RX_INTERRUPT.RXIPV4UDSBLPIS (RXIPV4UDSBLPIS)")
                    .WithFlag(5, out rxIpcPacketCounterInterrupt[(int)IpcCounter.IpV6Good], FieldMode.ReadToClear, name: "MMC_IPC_RX_INTERRUPT.RXIPV6GPIS (RXIPV6GPIS)")
                    .WithFlag(6, out rxIpcPacketCounterInterrupt[(int)IpcCounter.IpV6HeaderError], FieldMode.ReadToClear, name: "MMC_IPC_RX_INTERRUPT.RXIPV6HERPIS (RXIPV6HERPIS)")
                    .WithFlag(7, out rxIpcPacketCounterInterrupt[(int)IpcCounter.IpV6NoPayload], FieldMode.ReadToClear, name: "MMC_IPC_RX_INTERRUPT.RXIPV6NOPAYPIS (RXIPV6NOPAYPIS)")
                    .WithFlag(8, out rxIpcPacketCounterInterrupt[(int)IpcCounter.UdpGood], FieldMode.ReadToClear, name: "MMC_IPC_RX_INTERRUPT.RXUDPGPIS (RXUDPGPIS)")
                    .WithFlag(9, out rxIpcPacketCounterInterrupt[(int)IpcCounter.UdpError], FieldMode.ReadToClear, name: "MMC_IPC_RX_INTERRUPT.RXUDPERPIS (RXUDPERPIS)")
                    .WithFlag(10, out rxIpcPacketCounterInterrupt[(int)IpcCounter.TcpGood], FieldMode.ReadToClear, name: "MMC_IPC_RX_INTERRUPT.RXTCPGPIS (RXTCPGPIS)")
                    .WithFlag(11, out rxIpcPacketCounterInterrupt[(int)IpcCounter.TcpError], FieldMode.ReadToClear, name: "MMC_IPC_RX_INTERRUPT.RXTCPERPIS (RXTCPERPIS)")
                    .WithFlag(12, out rxIpcPacketCounterInterrupt[(int)IpcCounter.IcmpGood], FieldMode.ReadToClear, name: "MMC_IPC_RX_INTERRUPT.RXICMPGPIS (RXICMPGPIS)")
                    .WithFlag(13, out rxIpcPacketCounterInterrupt[(int)IpcCounter.IcmpError], FieldMode.ReadToClear, name: "MMC_IPC_RX_INTERRUPT.RXICMPERPIS (RXICMPERPIS)")
                    .WithReservedBits(14, 2)
                    .WithFlag(16, out rxIpcByteCounterInterrupt[(int)IpcCounter.IpV4Good], FieldMode.ReadToClear, name: "MMC_IPC_RX_INTERRUPT.RXIPV4GOIS (RXIPV4GOIS)")
                    .WithFlag(17, out rxIpcByteCounterInterrupt[(int)IpcCounter.IpV4HeaderError], FieldMode.ReadToClear, name: "MMC_IPC_RX_INTERRUPT.RXIPV4HEROIS (RXIPV4HEROIS)")
                    .WithFlag(18, out rxIpcByteCounterInterrupt[(int)IpcCounter.IpV4NoPayload], FieldMode.ReadToClear, name: "MMC_IPC_RX_INTERRUPT.RXIPV4NOPAYOIS (RXIPV4NOPAYOIS)")
                    .WithFlag(19, out rxIpcByteCounterInterrupt[(int)IpcCounter.IpV4Fragmented], FieldMode.ReadToClear, name: "MMC_IPC_RX_INTERRUPT.RXIPV4FRAGOIS (RXIPV4FRAGOIS)")
                    .WithFlag(20, out rxIpcByteCounterInterrupt[(int)IpcCounter.IpV4UDPChecksumDisabled], FieldMode.ReadToClear, name: "MMC_IPC_RX_INTERRUPT.RXIPV4UDSBLOIS (RXIPV4UDSBLOIS)")
                    .WithFlag(21, out rxIpcByteCounterInterrupt[(int)IpcCounter.IpV6Good], FieldMode.ReadToClear, name: "MMC_IPC_RX_INTERRUPT.RXIPV6GOIS (RXIPV6GOIS)")
                    .WithFlag(22, out rxIpcByteCounterInterrupt[(int)IpcCounter.IpV6HeaderError], FieldMode.ReadToClear, name: "MMC_IPC_RX_INTERRUPT.RXIPV6HEROIS (RXIPV6HEROIS)")
                    .WithFlag(23, out rxIpcByteCounterInterrupt[(int)IpcCounter.IpV6NoPayload], FieldMode.ReadToClear, name: "MMC_IPC_RX_INTERRUPT.RXIPV6NOPAYOIS (RXIPV6NOPAYOIS)")
                    .WithFlag(24, out rxIpcByteCounterInterrupt[(int)IpcCounter.UdpGood], FieldMode.ReadToClear, name: "MMC_IPC_RX_INTERRUPT.RXUDPGOIS (RXUDPGOIS)")
                    .WithFlag(25, out rxIpcByteCounterInterrupt[(int)IpcCounter.UdpError], FieldMode.ReadToClear, name: "MMC_IPC_RX_INTERRUPT.RXUDPEROIS (RXUDPEROIS)")
                    .WithFlag(26, out rxIpcByteCounterInterrupt[(int)IpcCounter.TcpGood], FieldMode.ReadToClear, name: "MMC_IPC_RX_INTERRUPT.RXTCPGOIS (RXTCPGOIS)")
                    .WithFlag(27, out rxIpcByteCounterInterrupt[(int)IpcCounter.TcpError], FieldMode.ReadToClear, name: "MMC_IPC_RX_INTERRUPT.RXTCPEROIS (RXTCPEROIS)")
                    .WithFlag(28, out rxIpcByteCounterInterrupt[(int)IpcCounter.IcmpGood], FieldMode.ReadToClear, name: "MMC_IPC_RX_INTERRUPT.RXICMPGOIS (RXICMPGOIS)")
                    .WithFlag(29, out rxIpcByteCounterInterrupt[(int)IpcCounter.IcmpError], FieldMode.ReadToClear, name: "MMC_IPC_RX_INTERRUPT.RXICMPEROIS (RXICMPEROIS)")
                    .WithReservedBits(30, 2)
                },
                {(long)RegistersMacAndMmc.RxIpv4GoodPackets, new DoubleWordRegister(this)
                    .WithValueField(0, 32, out rxIpcPacketCounter[(int)IpcCounter.IpV4Good], FieldMode.Read, name: "RXIPV4_GOOD_PACKETS.RXIPV4GDPKT (RXIPV4GDPKT)")
                },
                {(long)RegistersMacAndMmc.RxIpv4HeaderErrorPackets, new DoubleWordRegister(this)
                    .WithValueField(0, 32, out rxIpcPacketCounter[(int)IpcCounter.IpV4HeaderError], FieldMode.Read, name: "RXIPV4_HEADER_ERROR_PACKETS.RXIPV4HDRERRPKT (RXIPV4HDRERRPKT)")
                },
                {(long)RegistersMacAndMmc.RxIpv4NoPayloadPackets, new DoubleWordRegister(this)
                    .WithValueField(0, 32, out rxIpcPacketCounter[(int)IpcCounter.IpV4NoPayload], FieldMode.Read, name: "RXIPV4_NO_PAYLOAD_PACKETS.RXIPV4NOPAYPKT (RXIPV4NOPAYPKT)")
                },
                {(long)RegistersMacAndMmc.RxIpv4FragmentedPackets, new DoubleWordRegister(this)
                    .WithValueField(0, 32, out rxIpcPacketCounter[(int)IpcCounter.IpV4Fragmented], FieldMode.Read, name: "RXIPV4_FRAGMENTED_PACKETS.RXIPV4FRAGPKT (RXIPV4FRAGPKT)")
                },
                {(long)RegistersMacAndMmc.RxIpv4UdpChecksumDisabledPackets, new DoubleWordRegister(this)
                    .WithValueField(0, 32, out rxIpcPacketCounter[(int)IpcCounter.IpV4UDPChecksumDisabled], FieldMode.Read, name: "RXIPV4_UDP_CHECKSUM_DISABLED_PACKETS.RXIPV4UDSBLPKT (RXIPV4UDSBLPKT)")
                },
                {(long)RegistersMacAndMmc.RxIpv6GoodPackets, new DoubleWordRegister(this)
                    .WithValueField(0, 32, out rxIpcPacketCounter[(int)IpcCounter.IpV6Good], FieldMode.Read, name: "RXIPV6_GOOD_PACKETS.RXIPV6GDPKT (RXIPV6GDPKT)")
                },
                {(long)RegistersMacAndMmc.RxIpv6HeaderErrorPackets, new DoubleWordRegister(this)
                    .WithValueField(0, 32, out rxIpcPacketCounter[(int)IpcCounter.IpV6HeaderError], FieldMode.Read, name: "RXIPV6_HEADER_ERROR_PACKETS.RXIPV6HDRERRPKT (RXIPV6HDRERRPKT)")
                },
                {(long)RegistersMacAndMmc.RxIpv6NoPayloadPackets, new DoubleWordRegister(this)
                    .WithValueField(0, 32, out rxIpcPacketCounter[(int)IpcCounter.IpV6NoPayload], FieldMode.Read, name: "RXIPV6_NO_PAYLOAD_PACKETS.RXIPV6NOPAYPKT (RXIPV6NOPAYPKT)")
                },
                {(long)RegistersMacAndMmc.RxUdpGoodPackets, new DoubleWordRegister(this)
                    .WithValueField(0, 32, out rxIpcPacketCounter[(int)IpcCounter.UdpGood], FieldMode.Read, name: "RXUDP_GOOD_PACKETS.RXUDPGDPKT (RXUDPGDPKT)")
                },
                {(long)RegistersMacAndMmc.RxUdpErrorPackets, new DoubleWordRegister(this)
                    .WithValueField(0, 32, out rxIpcPacketCounter[(int)IpcCounter.UdpError], FieldMode.Read, name: "RXUDP_ERROR_PACKETS.RXUDPERRPKT (RXUDPERRPKT)")
                },
                {(long)RegistersMacAndMmc.RxTcpGoodPackets, new DoubleWordRegister(this)
                    .WithValueField(0, 32, out rxIpcPacketCounter[(int)IpcCounter.TcpGood], FieldMode.Read, name: "RXTCP_GOOD_PACKETS.RXTCPGDPKT (RXTCPGDPKT)")
                },
                {(long)RegistersMacAndMmc.RxTcpErrorPackets, new DoubleWordRegister(this)
                    .WithValueField(0, 32, out rxIpcPacketCounter[(int)IpcCounter.TcpError], FieldMode.Read, name: "RXTCP_ERROR_PACKETS.RXTCPERRPKT (RXTCPERRPKT)")
                },
                {(long)RegistersMacAndMmc.RxIcmpGoodPackets, new DoubleWordRegister(this)
                    .WithValueField(0, 32, out rxIpcPacketCounter[(int)IpcCounter.IcmpGood], FieldMode.Read, name: "RXICMP_GOOD_PACKETS.RXICMPGDPKT (RXICMPGDPKT)")
                },
                {(long)RegistersMacAndMmc.RxIcmpErrorPackets, new DoubleWordRegister(this)
                    .WithValueField(0, 32, out rxIpcPacketCounter[(int)IpcCounter.IcmpError], FieldMode.Read, name: "RXICMP_ERROR_PACKETS.RXICMPERRPKT (RXICMPERRPKT)")
                },
                {(long)RegistersMacAndMmc.RxIpv4GoodOctets, new DoubleWordRegister(this)
                    .WithValueField(0, 32, out rxIpcByteCounter[(int)IpcCounter.IpV4Good], FieldMode.Read, name: "RXIPV4_GOOD_OCTETS.RXIPV4GDOCT (RXIPV4GDOCT)")
                },
                {(long)RegistersMacAndMmc.RxIpv4HeaderErrorOctets, new DoubleWordRegister(this)
                    .WithValueField(0, 32, out rxIpcByteCounter[(int)IpcCounter.IpV4HeaderError], FieldMode.Read, name: "RXIPV4_HEADER_ERROR_OCTETS.RXIPV4HDRERROCT (RXIPV4HDRERROCT)")
                },
                {(long)RegistersMacAndMmc.RxIpv4NoPayloadOctets, new DoubleWordRegister(this)
                    .WithValueField(0, 32, out rxIpcByteCounter[(int)IpcCounter.IpV4NoPayload], FieldMode.Read, name: "RXIPV4_NO_PAYLOAD_OCTETS.RXIPV4NOPAYOCT (RXIPV4NOPAYOCT)")
                },
                {(long)RegistersMacAndMmc.RxIpv4FragmentedOctets, new DoubleWordRegister(this)
                    .WithValueField(0, 32, out rxIpcByteCounter[(int)IpcCounter.IpV4Fragmented], FieldMode.Read, name: "RXIPV4_FRAGMENTED_OCTETS.RXIPV4FRAGOCT (RXIPV4FRAGOCT)")
                },
                {(long)RegistersMacAndMmc.RxIpv4UdpChecksumDisableOctets, new DoubleWordRegister(this)
                    .WithValueField(0, 32, out rxIpcByteCounter[(int)IpcCounter.IpV4UDPChecksumDisabled], FieldMode.Read, name: "RXIPV4_UDP_CHECKSUM_DISABLE_OCTETS.RXIPV4UDSBLOCT (RXIPV4UDSBLOCT)")
                },
                {(long)RegistersMacAndMmc.RxIpv6GoodOctets, new DoubleWordRegister(this)
                    .WithValueField(0, 32, out rxIpcByteCounter[(int)IpcCounter.IpV6Good], FieldMode.Read, name: "RXIPV6_GOOD_OCTETS.RXIPV6GDOCT (RXIPV6GDOCT)")
                },
                {(long)RegistersMacAndMmc.RxIpv6HeaderErrorOctets, new DoubleWordRegister(this)
                    .WithValueField(0, 32, out rxIpcByteCounter[(int)IpcCounter.IpV6HeaderError], FieldMode.Read, name: "RXIPV6_HEADER_ERROR_OCTETS.RXIPV6HDRERROCT (RXIPV6HDRERROCT)")
                },
                {(long)RegistersMacAndMmc.RxIpv6NoPayloadOctets, new DoubleWordRegister(this)
                    .WithValueField(0, 32, out rxIpcByteCounter[(int)IpcCounter.IpV6NoPayload], FieldMode.Read, name: "RXIPV6_NO_PAYLOAD_OCTETS.RXIPV6NOPAYOCT (RXIPV6NOPAYOCT)")
                },
                {(long)RegistersMacAndMmc.RxUdpGoodOctets, new DoubleWordRegister(this)
                    .WithValueField(0, 32, out rxIpcByteCounter[(int)IpcCounter.UdpGood], FieldMode.Read, name: "RXUDP_GOOD_OCTETS.RXUDPGDOCT (RXUDPGDOCT)")
                },
                {(long)RegistersMacAndMmc.RxUdpErrorOctets, new DoubleWordRegister(this)
                    .WithValueField(0, 32, out rxIpcByteCounter[(int)IpcCounter.UdpError], FieldMode.Read, name: "RXUDP_ERROR_OCTETS.RXUDPERROCT (RXUDPERROCT)")
                },
                {(long)RegistersMacAndMmc.RxTcpGoodOctets, new DoubleWordRegister(this)
                    .WithValueField(0, 32, out rxIpcByteCounter[(int)IpcCounter.TcpGood], FieldMode.Read, name: "RXTCP_GOOD_OCTETS.RXTCPGDOCT (RXTCPGDOCT)")
                },
                {(long)RegistersMacAndMmc.RxTcpErrorOctets, new DoubleWordRegister(this)
                    .WithValueField(0, 32, out rxIpcByteCounter[(int)IpcCounter.TcpError], FieldMode.Read, name: "RXTCP_ERROR_OCTETS.RXTCPERROCT (RXTCPERROCT)")
                },
                {(long)RegistersMacAndMmc.RxIcmpGoodOctets, new DoubleWordRegister(this)
                    .WithValueField(0, 32, out rxIpcByteCounter[(int)IpcCounter.IcmpGood], FieldMode.Read, name: "RXICMP_GOOD_OCTETS.RXICMPGDOCT (RXICMPGDOCT)")
                },
                {(long)RegistersMacAndMmc.RxIcmpErrorOctets, new DoubleWordRegister(this)
                    .WithValueField(0, 32, out rxIpcByteCounter[(int)IpcCounter.IcmpError], FieldMode.Read, name: "RXICMP_ERROR_OCTETS.RXICMPERROCT (RXICMPERROCT)")
                },
                {(long)RegistersMacAndMmc.Layer3And4Control0, new DoubleWordRegister(this)
                    .WithTaggedFlag("MACL3L4C0R.L3PEN0 (L3PEN0)", 0)
                    .WithReservedBits(1, 1)
                    .WithTaggedFlag("MACL3L4C0R.L3SAM0 (L3SAM0)", 2)
                    .WithTaggedFlag("MACL3L4C0R.L3SAIM0 (L3SAIM0)", 3)
                    .WithTaggedFlag("MACL3L4C0R.L3DAM0 (L3DAM0)", 4)
                    .WithTaggedFlag("MACL3L4C0R.L3DAIM0 (L3DAIM0)", 5)
                    .WithTag("MACL3L4C0R.L3HSBM0 (L3HSBM0)", 6, 5)
                    .WithTag("MACL3L4C0R.L3HDBM0 (L3HDBM0)", 11, 5)
                    .WithTaggedFlag("MACL3L4C0R.L4PEN0 (L4PEN0)", 16)
                    .WithReservedBits(17, 1)
                    .WithTaggedFlag("MACL3L4C0R.L4SPM0 (L4SPM0)", 18)
                    .WithTaggedFlag("MACL3L4C0R.L4SPIM0 (L4SPIM0)", 19)
                    .WithTaggedFlag("MACL3L4C0R.L4DPM0 (L4DPM0)", 20)
                    .WithTaggedFlag("MACL3L4C0R.L4DPIM0 (L4DPIM0)", 21)
                    .WithReservedBits(22, 10)
                },
                {(long)RegistersMacAndMmc.Layer4AddressFilter0, new DoubleWordRegister(this)
                    .WithTag("MACL4A0R.L4SP0 (L4SP0)", 0, 16)
                    .WithTag("MACL4A0R.L4DP0 (L4DP0)", 16, 16)
                },
                {(long)RegistersMacAndMmc.Layer3Address0Filter0, new DoubleWordRegister(this)
                    .WithTag("MACL3A00R.L3A00 (L3A00)", 0, 32)
                },
                {(long)RegistersMacAndMmc.Layer3Address1Filter0, new DoubleWordRegister(this)
                    .WithTag("MACL3A10R.L3A10 (L3A10)", 0, 32)
                },
                {(long)RegistersMacAndMmc.Layer3Address2Filter0, new DoubleWordRegister(this)
                    .WithTag("MACL3A20.L3A20 (L3A20)", 0, 32)
                },
                {(long)RegistersMacAndMmc.Layer3Address3Filter0, new DoubleWordRegister(this)
                    .WithTag("MACL3A30.L3A30 (L3A30)", 0, 32)
                },
                {(long)RegistersMacAndMmc.Layer3And4Control1, new DoubleWordRegister(this)
                    .WithTaggedFlag("MACL3L4C1R.L3PEN1 (L3PEN1)", 0)
                    .WithReservedBits(1, 1)
                    .WithTaggedFlag("MACL3L4C1R.L3SAM1 (L3SAM1)", 2)
                    .WithTaggedFlag("MACL3L4C1R.L3SAIM1 (L3SAIM1)", 3)
                    .WithTaggedFlag("MACL3L4C1R.L3DAM1 (L3DAM1)", 4)
                    .WithTaggedFlag("MACL3L4C1R.L3DAIM1 (L3DAIM1)", 5)
                    .WithTag("MACL3L4C1R.L3HSBM1 (L3HSBM1)", 6, 5)
                    .WithTag("MACL3L4C1R.L3HDBM1 (L3HDBM1)", 11, 5)
                    .WithTaggedFlag("MACL3L4C1R.L4PEN1 (L4PEN1)", 16)
                    .WithReservedBits(17, 1)
                    .WithTaggedFlag("MACL3L4C1R.L4SPM1 (L4SPM1)", 18)
                    .WithTaggedFlag("MACL3L4C1R.L4SPIM1 (L4SPIM1)", 19)
                    .WithTaggedFlag("MACL3L4C1R.L4DPM1 (L4DPM1)", 20)
                    .WithTaggedFlag("MACL3L4C1R.L4DPIM1 (L4DPIM1)", 21)
                    .WithReservedBits(22, 10)
                },
                {(long)RegistersMacAndMmc.Layer4AddressFilter1, new DoubleWordRegister(this)
                    .WithTag("MACL4A1R.L4SP1 (L4SP1)", 0, 16)
                    .WithTag("MACL4A1R.L4DP1 (L4DP1)", 16, 16)
                },
                {(long)RegistersMacAndMmc.Layer3Address0Filter1, new DoubleWordRegister(this)
                    .WithTag("MACL3A01R.L3A01 (L3A01)", 0, 32)
                },
                {(long)RegistersMacAndMmc.Layer3Address1Filter1, new DoubleWordRegister(this)
                    .WithTag("MACL3A11R.L3A11 (L3A11)", 0, 32)
                },
                {(long)RegistersMacAndMmc.Layer3Address2Filter1, new DoubleWordRegister(this)
                    .WithTag("MACL3A21R.L3A21 (L3A21)", 0, 32)
                },
                {(long)RegistersMacAndMmc.Layer3Address3Filter1, new DoubleWordRegister(this)
                    .WithTag("MACL3A31R.L3A31 (L3A31)", 0, 32)
                },
                { (long)RegistersMacAndMmc.TimestampControl, new DoubleWordRegister(this, 0x2000)
                    .WithFlag(0, out enableTimestamp, name: "MACTSCR.TSENA (TSENA)",
                        changeCallback: (_, value) =>
                        {
                            if(!value)
                            {
                                // Only disable the timer here. According to the documentation timestamp timer
                                // starts counting only after setting the `TSINIT` bit
                                timestampSubsecondTimer.Enabled = false;
                            }
                        })
                    .WithFlag(1, out fineOrCoarseTimestampUpdate, name: "MACTSCR.TSCFUPDT (TSCFUPDT)")
                    .WithFlag(2, FieldMode.Read | FieldMode.WriteOneToClear, name: "MACTSCR.TSINIT (TSINIT)",
                        writeCallback: (_, value) =>
                        {
                            if(value)
                            {
                                SetTimestamp(systemTimeSecondUpdate.Value, systemTimeNanosecondUpdate.Value, init: true);
                            }
                        })
                    .WithFlag(3, FieldMode.Read | FieldMode.WriteOneToClear, name: "MACTSCR.TSUPDT (TSUPDT)",
                        writeCallback: (_, value) =>
                        {
                            if(value)
                            {
                                SetTimestamp(systemTimeSecondUpdate.Value, systemTimeNanosecondUpdate.Value, init: false);
                            }
                        })
                    .WithReservedBits(4, 1)
                    .WithFlag(5, name: "MACTSCR.TSADDREG (TSADDREG)",
                        valueProviderCallback: _ => false) // Addend is updated instantly
                    .WithReservedBits(6, 2)
                    .WithFlag(8, out enableTimestampForAll, name: "MACTSCR.TSENALL (TSENALL)")
                    .WithFlag(9, out timestampDigitalOrBinaryRollover, name: "MACTSCR.TSCTRLSSR (TSCTRLSSR)",
                        changeCallback: (_, value) => timestampSubsecondTimer.Limit = value ? DigitalSubsecondRollover : BinarySubsecondRollover)
                    .WithEnumField(10, 1, out usedPtpVersion, name: "MACTSCR.TSVER2ENA")
                    .WithFlag(11, out processPtpOverEthernet, name: "MACTSCR.TSIPENA (TSIPENA)")
                    .WithFlag(12, out processPtpOverIpv6, name: "MACTSCR.TSIPV6ENA (TSIPV6ENA)")
                    .WithFlag(13, out processPtpOverIpv4, name: "MACTSCR.TSIPV4ENA (TSIPV4ENA)")
                    .WithTaggedFlag("MACTSCR.TSEVNTENA (TSEVNTENA)", 14)
                    .WithTaggedFlag("MACTSCR.TSMSTRENA (TSMSTRENA)", 15)
                    .WithTag("MACTSCR.SNAPTYPSEL (SNAPTYPSEL)", 16, 2)
                    .WithTaggedFlag("MACTSCR.TSENMACADDR (TSENMACADDR)", 18)
                    .WithTaggedFlag("MACTSCR.CSC (CSC)", 19)
                    .WithReservedBits(20, 4)
                    .WithTaggedFlag("MACTSCR.TXTSSTSM (TXTSSTSM)", 24)
                    .WithReservedBits(25, 7)
                },
                {(long)RegistersMacAndMmc.SubsecondIncrement, new DoubleWordRegister(this)
                    .WithReservedBits(0, 8)
                    .WithTag("MACSSIR.SNSINC (SNSINC)", 8, 8)
                    .WithValueField(16, 8, out timestampSubsecondIncrement, name: "MACSSIR.SSINC (SSINC)")
                    .WithReservedBits(24, 8)
                },
                {(long)RegistersMacAndMmc.SystemTimeSeconds, new DoubleWordRegister(this)
                    .WithValueField(0, 32, FieldMode.Read, name: "MACSTSR.TSS (TSS)",
                        valueProviderCallback: _ =>
                        {
                            GetTimestamp(out var seconds, out _);
                            return seconds;
                        })
                },
                {(long)RegistersMacAndMmc.SystemTimeNanoseconds, new DoubleWordRegister(this)
                    .WithValueField(0, 31, FieldMode.Read, name: "MACSTNR.TSSS (TSSS)",
                        valueProviderCallback: _ =>
                        {
                            GetTimestamp(out _, out var nanoseconds);
                            return nanoseconds;
                        })
                    .WithReservedBits(31, 1)
                },
                {(long)RegistersMacAndMmc.SystemTimeSecondsUpdate, new DoubleWordRegister(this)
                    .WithValueField(0, 32, out systemTimeSecondUpdate, name: "MACSTSUR.TSS (TSS)")
                },
                {(long)RegistersMacAndMmc.SystemTimeNanosecondsUpdate, new DoubleWordRegister(this)
                    .WithValueField(0, 31, out systemTimeNanosecondUpdate, name: "MACSTNUR.TSSS (TSSS)")
                    .WithFlag(31, out addOrSubtractTime, name: "MACSTNUR.ADDSUB (ADDSUB)")
                },
                {(long)RegistersMacAndMmc.TimestampAddend, new DoubleWordRegister(this)
                    .WithValueField(0, 32, out timestampAddend, name: "MACTSAR.TSAR (TSAR)")
                },
                {(long)RegistersMacAndMmc.TimestampStatus, new DoubleWordRegister(this)
                    .WithTaggedFlag("MACTSSR.TSSOVF (TSSOVF)", 0)
                    .WithTaggedFlag("MACTSSR.TSTARGT0 (TSTARGT0)", 1)
                    .WithTaggedFlag("MACTSSR.AUXTSTRIG (AUXTSTRIG)", 2)
                    .WithTaggedFlag("MACTSSR.TSTRGTERR0 (TSTRGTERR0)", 3)
                    .WithReservedBits(4, 11)
                    .WithTaggedFlag("MACTSSR.TXTSSIS (TXTSSIS)", 15)
                    .WithTag("MACTSSR.ATSSTN (ATSSTN)", 16, 4)
                    .WithReservedBits(20, 4)
                    .WithTaggedFlag("MACTSSR.ATSSTM (ATSSTM)", 24)
                    .WithTag("MACTSSR.ATSNS (ATSNS)", 25, 5)
                    .WithReservedBits(30, 2)
                },
                {(long)RegistersMacAndMmc.TxTimestampStatusNanoseconds, new DoubleWordRegister(this)
                    .WithTag("MACTxTSSNR.TXTSSLO (TXTSSLO)", 0, 31)
                    .WithTaggedFlag("MACTxTSSNR.TXTSSMIS (TXTSSMIS)", 31)
                },
                {(long)RegistersMacAndMmc.TxTimestampStatusSeconds, new DoubleWordRegister(this)
                    .WithTag("MACTxTSSSR.TXTSSHI (TXTSSHI)", 0, 32)
                },
                {(long)RegistersMacAndMmc.AuxiliaryControl, new DoubleWordRegister(this)
                    .WithTaggedFlag("MACACR.ATSFC (ATSFC)", 0)
                    .WithReservedBits(1, 3)
                    .WithTaggedFlag("MACACR.ATSEN0 (ATSEN0)", 4)
                    .WithTaggedFlag("MACACR.ATSEN1 (ATSEN1)", 5)
                    .WithTaggedFlag("MACACR.ATSEN2 (ATSEN2)", 6)
                    .WithTaggedFlag("MACACR.ATSEN3 (ATSEN3)", 7)
                    .WithReservedBits(8, 24)
                },
                {(long)RegistersMacAndMmc.AuxiliaryTimestampNanoseconds, new DoubleWordRegister(this)
                    .WithTag("MACATSNR.AUXTSLO (AUXTSLO)", 0, 31)
                    .WithReservedBits(31, 1)
                },
                {(long)RegistersMacAndMmc.AuxiliaryTimestampSeconds, new DoubleWordRegister(this)
                    .WithTag("MACATSSR.AUXTSHI (AUXTSHI)", 0, 32)
                },
                {(long)RegistersMacAndMmc.TimestampIngressAsymmetricCorrection, new DoubleWordRegister(this)
                    .WithTag("MACTSIACR.OSTIAC (OSTIAC)", 0, 32)
                },
                {(long)RegistersMacAndMmc.TimestampEgressAsymmetricCorrection, new DoubleWordRegister(this)
                    .WithTag("MACTSEACR.OSTEAC (OSTEAC)", 0, 32)
                },
                {(long)RegistersMacAndMmc.TimestampIngressCorrectionNanosecond, new DoubleWordRegister(this)
                    .WithTag("MACTSICNR.TSIC (TSIC)", 0, 32)
                },
                {(long)RegistersMacAndMmc.TimestampEgressCorrectionNanosecond, new DoubleWordRegister(this)
                    .WithTag("MACTSECNR.TSEC (TSEC)", 0, 32)
                },
                {(long)RegistersMacAndMmc.PPSControl, new DoubleWordRegister(this)
                    .WithTag("MACPPSCR.PPSCTRL/MACPPSCR.PPSCMD (PPSCTRL/PPSCMD)", 0, 4)
                    .WithTaggedFlag("MACPPSCR.PPSEN0 (PPSEN0)", 4)
                    .WithTag("MACPPSCR.TRGTMODSEL0 (TRGTMODSEL0)", 5, 2)
                    .WithReservedBits(7, 25)
                },
                {(long)RegistersMacAndMmc.PPSTargetTimeSeconds, new DoubleWordRegister(this)
                    .WithTag("MACPPSTTSR.TSTRH0 (TSTRH0)", 0, 31)
                    .WithReservedBits(31, 1)
                },
                {(long)RegistersMacAndMmc.PPSTargetTimeNanoseconds, new DoubleWordRegister(this)
                    .WithTag("MACPPSTTNR.TTSL0 (TTSL0)", 0, 31)
                    .WithTaggedFlag("MACPPSTTNR.TRGTBUSY0 (TRGTBUSY0)", 31)
                },
                {(long)RegistersMacAndMmc.PPSInterval, new DoubleWordRegister(this)
                    .WithTag("MACPPSIR.PPSINT0 (PPSINT0)", 0, 32)
                },
                {(long)RegistersMacAndMmc.PPSWidth, new DoubleWordRegister(this)
                    .WithTag("MACPPSWR.PPSWIDTH0 (PPSWIDTH0)", 0, 32)
                },
                {(long)RegistersMacAndMmc.PTPOffloadControl, new DoubleWordRegister(this)
                    .WithTaggedFlag("MACPOCR.PTOEN (PTOEN)", 0)
                    .WithTaggedFlag("MACPOCR.ASYNCEN (ASYNCEN)", 1)
                    .WithTaggedFlag("MACPOCR.APDREQEN (APDREQEN)", 2)
                    .WithReservedBits(3, 1)
                    .WithTaggedFlag("MACPOCR.ASYNCTRIG (ASYNCTRIG)", 4)
                    .WithTaggedFlag("MACPOCR.APDREQTRIG (APDREQTRIG)", 5)
                    .WithTaggedFlag("MACPOCR.DRRDIS (DRRDIS)", 6)
                    .WithReservedBits(7, 1)
                    .WithTag("MACPOCR.DN (DN)", 8, 8)
                    .WithReservedBits(16, 16)
                },
                {(long)RegistersMacAndMmc.PTPSourcePortIdentity0, new DoubleWordRegister(this)
                    .WithTag("MACSPI0R.SPI0 (SPI0)", 0, 32)
                },
                {(long)RegistersMacAndMmc.PTPSourcePortIdentity1, new DoubleWordRegister(this)
                    .WithTag("MACSPI1R.SPI1 (SPI1)", 0, 32)
                },
                {(long)RegistersMacAndMmc.PTPSourcePortIdentity2, new DoubleWordRegister(this)
                    .WithTag("MACSPI2R.SPI2 (SPI2)", 0, 16)
                    .WithReservedBits(16, 16)
                },
                {(long)RegistersMacAndMmc.LogMessageInterval, new DoubleWordRegister(this)
                    .WithTag("MACLMIR.LSI (LSI)", 0, 8)
                    .WithTag("MACLMIR.DRSYNCR (DRSYNCR)", 8, 3)
                    .WithReservedBits(11, 13)
                    .WithTag("MACLMIR.LMPDRI (LMPDRI)", 24, 8)
                }
            };
        }

        private IDictionary<long, DoubleWordRegister> CreateMTLRegisterMap()
        {
            return new Dictionary<long, DoubleWordRegister>()
            {
                {(long)RegistersMTL.OperatingMode, new DoubleWordRegister(this)
                    .WithReservedBits(0, 1)
                    .WithTaggedFlag("MTLOMR.DTXSTS (DTXSTS)", 1)
                    .WithReservedBits(2, 6)
                    .WithTaggedFlag("MTLOMR.CNTPRST (CNTPRST)", 8)
                    .WithTaggedFlag("MTLOMR.CNTCLR (CNTCLR)", 9)
                    .WithReservedBits(10, 22)
                },
                {(long)RegistersMTL.InterruptStatus, new DoubleWordRegister(this)
                    .WithTaggedFlag("MTLISR.Q0IS (Queue interrupt status)", 0)
                    .WithReservedBits(1, 31)
                    .WithChangeCallback((_, __) => UpdateInterrupts())
                },
                {(long)RegistersMTL.TxQueueOperating, new DoubleWordRegister(this, 0x70008)
                    .WithTaggedFlag("MTLTxQOMR.FTQ (Flush Transmit Queue)", 0)
                    .WithTaggedFlag("MTLTxQOMR.TSF (Transmit Store and Forward)", 1)
                    .WithTag("MTLTxQOMR.TXQEN (Transmit Queue Enable)", 2, 2)
                    .WithTag("MTLTxQOMR.TTC (Transmit Threshold Control)", 4, 3)
                    .WithReservedBits(7, 9)
                    .WithTag("MTLTxQOMR.TQS (Transmit Queue Size)", 16, 3)
                    .WithReservedBits(19, 13)
                },
                {(long)RegistersMTL.TxQueueUnderflow, new DoubleWordRegister(this)
                    .WithTag("MTLTxQUR.UFFRMCNT (Underflow Packet Counter)", 0, 11)
                    .WithTaggedFlag("MTLTxQUR.UFCNTOVF (UFCNTOVF)", 11)
                    .WithReservedBits(12, 20)
                },
                {(long)RegistersMTL.TxQueueDebug, new DoubleWordRegister(this)
                    // values are intentionally empty because we send packets immediately
                    .WithFlag(0, FieldMode.Read, valueProviderCallback: _ => false, name: "MTLTxQDR.TXQPAUSED (TXQPAUSED)")
                    .WithEnumField<DoubleWordRegister, MTLTxQueueReadControllerStatus>(1, 2, FieldMode.Read,
                        valueProviderCallback: _ => MTLTxQueueReadControllerStatus.Idle, name: "MTLTxQDR.TRCSTS (TRCSTS)")
                    .WithFlag(3, FieldMode.Read, valueProviderCallback: _ => false, name: "MTLTxQDR.TWCSTS (TWCSTS)")
                    .WithFlag(4, FieldMode.Read, valueProviderCallback: _ => false, name: "MTLTxQDR.TXQSTS (TXQSTS)")
                    .WithFlag(5, FieldMode.Read, valueProviderCallback: _ => false, name: "MTLTxQDR.TXSTSFSTS (TXSTSFSTS)")
                    .WithReservedBits(6, 10)
                    .WithValueField(16, 3, FieldMode.Read, valueProviderCallback: _ => 0, name: "MTLTxQDR.PTXQ (PTXQ)")
                    .WithReservedBits(19, 1)
                    .WithValueField(20, 3, FieldMode.Read, valueProviderCallback: _ => 0, name: "MTLTxQDR.STXSTSF (STXSTSF)")
                    .WithReservedBits(23, 9)
                },
                {(long)RegistersMTL.QueueInterruptControlStatus, new DoubleWordRegister(this)
                    .WithTaggedFlag("MTLQICSR.TXUNFIS (TXUNFIS)", 0)
                    .WithReservedBits(1, 7)
                    .WithTaggedFlag("MTLQICSR.TXUIE (TXUIE)", 8)
                    .WithReservedBits(9, 7)
                    .WithTaggedFlag("MTLQICSR.RXOVFIS (RXOVFIS)", 16)
                    .WithReservedBits(17, 7)
                    .WithTaggedFlag("MTLQICSR.RXOIE (RXOIE)", 24)
                    .WithReservedBits(25, 7)
                },
                {(long)RegistersMTL.RxQueueOperatingMode, new DoubleWordRegister(this)
                    .WithTag("MTLRxQOMR.RTC (RTC)", 0, 2)
                    .WithReservedBits(2, 1)
                    .WithTaggedFlag("MTLRxQOMR.FUP (FUP)", 3)
                    .WithTaggedFlag("MTLRxQOMR.FEP (FEP)", 4)
                    .WithTaggedFlag("MTLRxQOMR.RSF (RSF)", 5)
                    .WithTaggedFlag("MTLRxQOMR.DIS_TCP_EF (DIS_TCP_EF)", 6)
                    .WithTaggedFlag("MTLRxQOMR.EHFC (EHFC)", 7)
                    .WithTag("MTLRxQOMR.RFA (RFA)", 8, 4)
                    .WithReservedBits(12, 2)
                    .WithTag("MTLRxQOMR.RFD (RFD)", 14, 4)
                    .WithReservedBits(18, 2)
                    .WithValueField(20, 5, FieldMode.Read, valueProviderCallback: _ => 0b11111, name: "MTLRxQOMR.RQS (RQS)")
                    .WithReservedBits(25, 7)
                },
                {(long)RegistersMTL.RxQueueMissedPacketAndOverflowCounter, new DoubleWordRegister(this)
                    .WithTag("MTLRxQMPOCR.OVFPKTCNT (OVFPKTCNT)", 0, 11)
                    .WithTaggedFlag("MTLRxQMPOCR.OVFCNTOVF (OVFCNTOVF)", 11)
                    .WithReservedBits(12, 4)
                    .WithTag("MTLRxQMPOCR.MISPKTCNT (MISPKTCNT)", 16, 11)
                    .WithTaggedFlag("MTLRxQMPOCR.MISCNTOVF (MISCNTOVF)", 27)
                    .WithReservedBits(28, 4)
                },
                {(long)RegistersMTL.RxQueueDebug, new DoubleWordRegister(this)
                    .WithTaggedFlag("MTLRxQDR.RWCSTS (RWCSTS)", 0)
                    .WithTag("MTLRxQDR.RRCSTS (RRCSTS)", 1, 2)
                    .WithReservedBits(3, 1)
                    .WithTag("MTLRxQDR.RXQSTS (RXQSTS)", 4, 2)
                    .WithReservedBits(6, 10)
                    .WithTag("MTLRxQDR.PRXQ (PRXQ)", 16, 14)
                    .WithReservedBits(30, 2)
                }
            };
        }

        private IDictionary<long, DoubleWordRegister> CreateDMARegisterMap()
        {
            var map = new Dictionary<long, DoubleWordRegister>()
            {
                {(long)RegistersDMA.DMAMode, new DoubleWordRegister(this)
                    .WithFlag(0, FieldMode.Read | FieldMode.Set, writeCallback: (_, value) =>
                    {
                        if(value)
                        {
                            this.Log(LogLevel.Info, "Software reset.");
                            SoftwareReset();
                        }
                    },
                    valueProviderCallback: _ => false, name: "DMAMR.SWR (Software Reset)")
                    .WithTaggedFlag("DMAMR.DA (DMA Tx or Rx Arbitration Scheme)", 1)
                    .WithReservedBits(2, 9)
                    .WithTaggedFlag("DMAMR.TXPR (Transmit priority)", 11)
                    .WithTag("DMAMR.PR (Priority ratio)", 12, 3)
                    .WithReservedBits(15, 1)
                    .WithEnumField(16, 2, out dmaInterruptMode, name: "DMAMR.INTM (Interrupt Mode)",
                        changeCallback: (previous, current) =>
                        {
                            if(current == DMAChannelInterruptMode.Reserved)
                            {
                                this.Log(LogLevel.Warning, "Attempted to set DMA interrupt mode to a reserved value. Reverting back to the previous value {0}", previous);
                                dmaInterruptMode.Value = previous;
                                return;
                            }

                            UpdateInterrupts();
                        })
                    .WithReservedBits(18, 14)
                },
                {(long)RegistersDMA.SystemBusMode, new DoubleWordRegister(this)
                    .WithTaggedFlag("DMASBMR.FB (Fixed Burst Length)", 0)
                    .WithReservedBits(1, 11)
                    .WithFlag(12, name: "DMASBMR.AAL (Address-Aligned Beats)")
                    .WithReservedBits(13, 1)
                    .WithTaggedFlag("DMASBMR.MB (Mixed Burst)", 14)
                    .WithTaggedFlag("DMASBMR.RB (Rebuild INCRx Burst)", 15)
                    .WithReservedBits(16, 16)
                },
                {(long)RegistersDMA.InterruptStatus, new DoubleWordRegister(this)
                    .WithFlags(0, dmaChannels.Length, name: "DMAISR.DCxIS (DMA Channel Interrupt Status)",
                        valueProviderCallback: (i, _) => dmaChannels[i].Interrupts)
                    .WithReservedBits(dmaChannels.Length, 16 - dmaChannels.Length)
                    .WithTaggedFlag("DMAISR.MTLIS (MTL Interrupt Status)", 16)
                    .WithTaggedFlag("DMAISR.MACIS (MAC Interrupt Status)", 17)
                    .WithReservedBits(18, 14)
                    .WithChangeCallback((_, __) => UpdateInterrupts())
                },
                {(long)RegistersDMA.DebugStatus0, new DoubleWordRegister(this)
                    .WithTaggedFlag("DMADSR.AXWHSTS (AHB Master Write Channel)", 0)
                    .WithReservedBits(1, 7)
                    .For((r, i) =>
                    {
                        var offset = i * 8;
                        r.WithEnumField<DoubleWordRegister, DMARxProcessState>(8 + offset, 4, FieldMode.Read,
                            valueProviderCallback: _ => dmaChannels[i].DmaRxState, name: $"DMADSR.RPS{i} (DMA Channel {i} Receive Process State)")
                         .WithEnumField<DoubleWordRegister, DMATxProcessState>(12 + offset, 4, FieldMode.Read,
                            valueProviderCallback: _ => dmaChannels[i].DmaTxState, name: $"DMADSR.TPS{i} (DMA Channel {i} Transmit Process State)");
                    }, 0, Math.Min(3, dmaChannels.Length))
                },
            };

            const int maxDMAChannelsPerDebugRegister = 4;
            // Start from channel 3 as channels 0, 1, 2 fit in DebugStatus0
            for(var channelId = 3; channelId < dmaChannels.Length; channelId++)
            {
                var bitOffset = 8 * ((channelId + 1) % maxDMAChannelsPerDebugRegister);
                var registerOffset = (long)RegistersDMA.DebugStatus0 + 4 * ((channelId + 1) / maxDMAChannelsPerDebugRegister);
                if(!map.TryGetValue(registerOffset, out var register))
                {
                    register = new DoubleWordRegister(this);
                    map[registerOffset] = register;
                }

                register
                    .WithEnumField<DoubleWordRegister, DMARxProcessState>(bitOffset, 4, FieldMode.Read,
                        valueProviderCallback: _ => dmaChannels[channelId].DmaRxState, name: $"DMADSR.RPS{channelId} (DMA Channel {channelId} Receive Process State)")
                    .WithEnumField<DoubleWordRegister, DMATxProcessState>(4 + bitOffset, 4, FieldMode.Read,
                        valueProviderCallback: _ => dmaChannels[channelId].DmaTxState, name: $"DMADSR.TPS{channelId} (DMA Channel {channelId} Transmit Process State)");
            }

            foreach(var channel in dmaChannels)
            {
                channel.DefineChannelRegisters(ref map);
            }

            return map;
        }

        private uint Read<T>(DoubleWordRegisterCollection registersCollection, string regionName, long offset)
        where T : struct, IComparable, IFormattable
        {
            var result = 0U;
            try
            {
                if(registersCollection.TryRead(offset, out result))
                {
                    return result;
                }
            }
            finally
            {
                if(PeripheralAccessLogLevel != null)
                {
                    this.Log(PeripheralAccessLogLevel, "Read from {0} at offset 0x{1:X} ({2}), returned 0x{3:X}.", regionName, offset, Enum.Format(typeof(T), offset, "G"), result);
                }
            }
            this.Log(LogLevel.Warning, "Unhandled read from {0} at offset 0x{1:X} ({2}).", regionName, offset, Enum.Format(typeof(T), offset, "G"));
            return result;
        }

        private void Write<T>(DoubleWordRegisterCollection registersCollection, string regionName, long offset, uint value)
        where T : struct, IComparable, IFormattable
        {
            if(PeripheralAccessLogLevel != null)
            {
                this.Log(PeripheralAccessLogLevel, "Write to {0} at offset 0x{1:X} ({2}), value 0x{3:X}.", regionName, offset, Enum.Format(typeof(T), offset, "G"), value);
            }
            if(!registersCollection.TryWrite(offset, value))
            {
                this.Log(LogLevel.Warning, "Unhandled write to {0} at offset 0x{1:X} ({2}), value 0x{3:X}.", regionName, offset, Enum.Format(typeof(T), offset, "G"), value);
                return;
            }
        }

        private void ExecuteMIIOperation()
        {
            if(!miiBusy.Value)
            {
                return;
            }
            miiBusy.Value = false;

            switch(miiOperation.Value)
            {
            case MIIOperation.Read:
                if(clause45PhyEnable.Value)
                {
                    if(!TryGetPhy<Clause45Address, ushort>((uint)miiPhy.Value, out var phy))
                    {
                        this.Log(LogLevel.Debug, "Read access to unknown phy {0} via Clause 45.", miiPhy.Value);
                        break;
                    }
                    var c45Address = new Clause45Address((byte)miiRegisterOrDeviceAddress.Value, (ushort)miiAddress.Value);
                    miiData.Value = phy.Read(c45Address);
                    this.Log(LogLevel.Noisy, "Read ({1}, 0x{2:X}) access to phy {0} via Clause 45.", miiPhy.Value, c45Address, miiData.Value);
                }
                else
                {
                    if(!TryGetPhy<ushort>((uint)miiPhy.Value, out var phy))
                    {
                        this.Log(LogLevel.Debug, "Read access to unknown phy {0} via Clause 22.", miiPhy.Value);
                        break;
                    }
                    miiData.Value = phy.Read((ushort)miiRegisterOrDeviceAddress.Value);
                    this.Log(LogLevel.Noisy, "Read ({1}, 0x{2:X}) access to phy {0} via Clause 22.", miiPhy.Value, miiRegisterOrDeviceAddress.Value, miiData.Value);
                }
                break;
            case MIIOperation.Write:
                if(clause45PhyEnable.Value)
                {
                    if(!TryGetPhy<Clause45Address, ushort>((uint)miiPhy.Value, out var phy))
                    {
                        this.Log(LogLevel.Debug, "Write access to unknown phy {0} via Clause 45.", miiPhy.Value);
                        break;
                    }
                    var c45Address = new Clause45Address((byte)miiRegisterOrDeviceAddress.Value, (ushort)miiAddress.Value);
                    this.Log(LogLevel.Noisy, "Write ({1}, 0x{2:X}) access to phy {0} via Clause 45.", miiPhy.Value, c45Address, miiData.Value);
                    phy.Write(c45Address, (ushort)miiData.Value);
                }
                else
                {
                    if(!TryGetPhy<ushort>((uint)miiPhy.Value, out var phy))
                    {
                        this.Log(LogLevel.Debug, "Write access to unknown phy {0} via Clause 22.", miiPhy.Value);
                        break;
                    }
                    this.Log(LogLevel.Noisy, "Write ({1}, 0x{2:X}) access to phy {0} via Clause 22.", miiPhy.Value, miiRegisterOrDeviceAddress.Value, miiData.Value);
                    phy.Write((ushort)miiRegisterOrDeviceAddress.Value, (ushort)miiData.Value);
                }
                break;
            case MIIOperation.PostReadAddressIncrement:
                if(!clause45PhyEnable.Value)
                {
                    this.Log(LogLevel.Warning, "Invalid MII Command: Post Read Increment Address is valid only for Clause 45 PHY.");
                    break;
                }
                this.Log(LogLevel.Warning, "Unimplemented Command: Post Read Increment Address.");
                break;
            default:
                this.Log(LogLevel.Warning, "Invalid MII Command: Reserved Operation (0x{0:X}).", miiOperation.Value);
                break;
            }
        }

        private IFlagRegisterField rxEnable;
        private IFlagRegisterField txEnable;
        private IFlagRegisterField loopbackEnabled;
        private IFlagRegisterField checksumOffloadEnable;
        private IFlagRegisterField crcCheckDisable;
        private IFlagRegisterField packetDuplicationControl;
        private IFlagRegisterField ptpMessageTypeInterrupt;
        private IFlagRegisterField lowPowerIdleInterrupt;
        private IFlagRegisterField timestampInterrupt;
        private IFlagRegisterField ptpMessageTypeInterruptEnable;
        private IFlagRegisterField lowPowerIdleInterruptEnable;
        private IFlagRegisterField timestampInterruptEnable;
        private IFlagRegisterField miiBusy;
        private IFlagRegisterField clause45PhyEnable;
        private IEnumRegisterField<MIIOperation> miiOperation;
        private IEnumRegisterField<RegisterSourceAddressOperation> sourceAddressOperation;
        private IValueRegisterField miiRegisterOrDeviceAddress;
        private IValueRegisterField miiPhy;
        private IValueRegisterField miiData;
        private IValueRegisterField miiAddress;
        private IValueRegisterField dmaChannelSelect;
        private IFlagRegisterField rxUnicastPacketCounterInterrupt;
        private IFlagRegisterField rxFifoPacketCounterInterrupt;
        private IFlagRegisterField rxCrcErrorPacketCounterInterrupt;
        private IFlagRegisterField rxMulticastPacketCounterInterrupt;
        private IFlagRegisterField rxBroadcastPacketCounterInterrupt;
        private IFlagRegisterField rxGoodByteCounterInterrupt;
        private IFlagRegisterField rxByteCounterInterrupt;
        private IFlagRegisterField rxPacketCounterInterrupt;
        private IFlagRegisterField txGoodPacketCounterInterrupt;
        private IFlagRegisterField txGoodByteCounterInterrupt;
        private IFlagRegisterField txBroadcastPacketCounterInterrupt;
        private IFlagRegisterField txMulticastPacketCounterInterrupt;
        private IFlagRegisterField txUnicastPacketCounterInterrupt;
        private IFlagRegisterField txPacketCounterInterrupt;
        private IFlagRegisterField txByteCounterInterrupt;
        private IFlagRegisterField rxUnicastPacketCounterInterruptEnable;
        private IFlagRegisterField rxFifoPacketCounterInterruptEnable;
        private IFlagRegisterField rxCrcErrorPacketCounterInterruptEnable;
        private IFlagRegisterField rxMulticastPacketCounterInterruptEnable;
        private IFlagRegisterField rxBroadcastPacketCounterInterruptEnable;
        private IFlagRegisterField rxGoodByteCounterInterruptEnable;
        private IFlagRegisterField rxByteCounterInterruptEnable;
        private IFlagRegisterField rxPacketCounterInterruptEnable;
        private IFlagRegisterField txGoodPacketCounterInterruptEnable;
        private IFlagRegisterField txGoodByteCounterInterruptEnable;
        private IFlagRegisterField txBroadcastPacketCounterInterruptEnable;
        private IFlagRegisterField txMulticastPacketCounterInterruptEnable;
        private IFlagRegisterField txUnicastPacketCounterInterruptEnable;
        private IFlagRegisterField txPacketCounterInterruptEnable;
        private IFlagRegisterField txByteCounterInterruptEnable;
        private IValueRegisterField txByteCounter;
        private IValueRegisterField txPacketCounter;
        private IValueRegisterField txUnicastPacketCounter;
        private IValueRegisterField txMulticastPacketCounter;
        private IValueRegisterField txBroadcastPacketCounter;
        private IValueRegisterField txGoodByteCounter;
        private IValueRegisterField txGoodPacketCounter;
        private IValueRegisterField rxPacketCounter;
        private IValueRegisterField rxByteCounter;
        private IValueRegisterField rxGoodByteCounter;
        private IValueRegisterField rxBroadcastPacketCounter;
        private IValueRegisterField rxMulticastPacketCounter;
        private IValueRegisterField rxCrcErrorPacketCounter;
        private IValueRegisterField rxUnicastPacketCounter;
        private IValueRegisterField rxFifoPacketCounter;
        private IFlagRegisterField enableTimestamp;
        private IFlagRegisterField enableTimestampForAll;
        private IFlagRegisterField timestampDigitalOrBinaryRollover;
        private IEnumRegisterField<PTPVersion> usedPtpVersion;
        private IValueRegisterField systemTimeSecondUpdate;
        private IValueRegisterField systemTimeNanosecondUpdate;
        private IFlagRegisterField addOrSubtractTime;
        private IValueRegisterField timestampAddend;
        private IFlagRegisterField processPtpOverEthernet;
        private IFlagRegisterField processPtpOverIpv4;
        private IFlagRegisterField processPtpOverIpv6;
        private IFlagRegisterField fineOrCoarseTimestampUpdate;
        private IValueRegisterField timestampSubsecondIncrement;

        private readonly IFlagRegisterField[] rxIpcPacketCounterInterruptEnable;
        private readonly IFlagRegisterField[] rxIpcByteCounterInterruptEnable;
        private readonly IFlagRegisterField[] rxIpcPacketCounterInterrupt;
        private readonly IFlagRegisterField[] rxIpcByteCounterInterrupt;
        private readonly IValueRegisterField[] rxIpcPacketCounter;
        private readonly IValueRegisterField[] rxIpcByteCounter;

        private readonly DoubleWordRegisterCollection macAndMmcRegisters;
        // MAC Transaction Layer
        private readonly DoubleWordRegisterCollection mtlRegisters;
        private readonly DoubleWordRegisterCollection dmaRegisters;

        private readonly int NumberOfIpcCounters = Enum.GetNames(typeof(IpcCounter)).Length;

        [RegistersDescription]
        public enum RegistersMacAndMmc : long
        {
            OperatingModeConfiguration = 0x000,
            ExtendedOperatingModeConfiguration = 0x004,
            PacketFilteringControl = 0x008,
            WatchdogTimeout = 0x00C,
            HashTable0 = 0x010,
            HashTable1 = 0x014,
            VLANTag = 0x050,
            VLANHashTable = 0x058,
            VLANInclusion = 0x060,
            InnerVLANInclusion = 0x064,
            TxQueueFlowControl = 0x070,
            RxFlowControl = 0x090,
            InterruptStatus = 0x0B0,
            InterruptEnable = 0x0B4,
            RxTxStatus = 0x0B8,
            PMTControlStatus = 0x0C0,
            RemoteWakeUpPacketFilter = 0x0C4,
            LPIControlAndStatus = 0x0D0,
            LPITimersControl = 0x0D4,
            LPIEntryTimer = 0x0D8,
            OneMicrosecondTickCounter = 0x0DC,
            Version = 0x110,
            Debug = 0x114,
            HardwareFeature0 = 0x11C,
            HardwareFeature1 = 0x120,
            HardwareFeature2 = 0x124,
            HardwareFeature3 = 0x128,
            MDIOAddress = 0x200,
            MDIOData = 0x204,
            ARPAddress = 0x210,
            CSRSoftwareControl = 0x230,
            MACAddress0High = 0x300,
            MACAddress0Low = 0x304,
            MACAddress1Low = 0x30C,
            MACAddress2Low = 0x314,
            MACAddress1High = 0x308,
            MACAddress2High = 0x310,
            MACAddress3High = 0x318,
            MACAddress3Low = 0x31C,
            MMCControl = 0x700,
            MMCRxInterrupt = 0x704,
            MMCTxInterrupt = 0x708,
            MMCRxInterruptMask = 0x70C,
            MMCTxInterruptMask = 0x710,
            TxOctetCountGoodBad = 0x714,
            TxPacketCountGoodBad = 0x718,
            TxUnicastPacketsGoodBad = 0x73C,
            TxMulticastPacketsGoodBad = 0x740,
            TxBroadcastPacketsGoodBad = 0x744,
            TxSingleCollisionGoodPackets = 0x74C,
            TxMultipleCollisionGoodPackets = 0x750,
            TxOctetCountGood = 0x764,
            TxPacketCountGood = 0x768,
            RxPacketsCountGoodBad = 0x780,
            RxOctetCountGoodBad = 0x784,
            RxOctetCountGood = 0x788,
            RxBroadcastPacketsGood = 0x78C,
            RxMulticastPacketsGood = 0x790,
            RxCRCErrorPackets = 0x794,
            RxAlignmentErrorPackets = 0x798,
            RxUnicastPacketsGood = 0x7C4,
            RxFifoOverflowPackets = 0x7D4,
            TxLPIMicrodecondTimer = 0x7EC,
            TxLPITransitionCounter = 0x7F0,
            RxLPIMicrosecondCounter = 0x7F4,
            RxLPITransitionCounter = 0x7F8,
            MmcIpcRxInterruptMask = 0x800,
            MmcIpcRxInterrupt = 0x808,
            RxIpv4GoodPackets = 0x810,
            RxIpv4HeaderErrorPackets = 0x814,
            RxIpv4NoPayloadPackets = 0x818,
            RxIpv4FragmentedPackets = 0x81C,
            RxIpv4UdpChecksumDisabledPackets = 0x820,
            RxIpv6GoodPackets = 0x824,
            RxIpv6HeaderErrorPackets = 0x828,
            RxIpv6NoPayloadPackets = 0x82C,
            RxUdpGoodPackets = 0x830,
            RxUdpErrorPackets = 0x834,
            RxTcpGoodPackets = 0x838,
            RxTcpErrorPackets = 0x83C,
            RxIcmpGoodPackets = 0x840,
            RxIcmpErrorPackets = 0x844,
            RxIpv4GoodOctets = 0x850,
            RxIpv4HeaderErrorOctets = 0x854,
            RxIpv4NoPayloadOctets = 0x858,
            RxIpv4FragmentedOctets = 0x85C,
            RxIpv4UdpChecksumDisableOctets = 0x860,
            RxIpv6GoodOctets = 0x864,
            RxIpv6HeaderErrorOctets = 0x868,
            RxIpv6NoPayloadOctets = 0x86C,
            RxUdpGoodOctets = 0x870,
            RxUdpErrorOctets = 0x874,
            RxTcpGoodOctets = 0x878,
            RxTcpErrorOctets = 0x87C,
            RxIcmpGoodOctets = 0x880,
            RxIcmpErrorOctets = 0x884,
            Layer3And4Control0 = 0x900,
            Layer4AddressFilter0 = 0x904,
            Layer3Address0Filter0 = 0x910,
            Layer3Address1Filter0 = 0x914,
            Layer3Address2Filter0 = 0x918,
            Layer3Address3Filter0 = 0x91C,
            Layer3And4Control1 = 0x930,
            Layer4AddressFilter1 = 0x934,
            Layer3Address0Filter1 = 0x940,
            Layer3Address1Filter1 = 0x944,
            Layer3Address2Filter1 = 0x948,
            Layer3Address3Filter1 = 0x94C,
            TimestampControl = 0xB00,
            SubsecondIncrement = 0xB04,
            SystemTimeSeconds = 0xB08,
            SystemTimeNanoseconds = 0xB0C,
            SystemTimeSecondsUpdate = 0xB10,
            SystemTimeNanosecondsUpdate = 0xB14,
            TimestampAddend = 0xB18,
            TimestampStatus = 0xB20,
            TxTimestampStatusNanoseconds = 0xB30,
            TxTimestampStatusSeconds = 0xB34,
            AuxiliaryControl = 0xB40,
            AuxiliaryTimestampNanoseconds = 0xB48,
            AuxiliaryTimestampSeconds = 0xB4C,
            TimestampIngressAsymmetricCorrection = 0xB50,
            TimestampEgressAsymmetricCorrection = 0xB54,
            TimestampIngressCorrectionNanosecond = 0xB58,
            TimestampEgressCorrectionNanosecond = 0xB5C,
            PPSControl = 0xB70,
            PPSTargetTimeSeconds = 0xB80,
            PPSTargetTimeNanoseconds = 0xB84,
            PPSInterval = 0xB88,
            PPSWidth = 0xB8C,
            PTPOffloadControl = 0xBC0,
            PTPSourcePortIdentity0 = 0xBC4,
            PTPSourcePortIdentity1 = 0xBC8,
            PTPSourcePortIdentity2 = 0xBCC,
            LogMessageInterval = 0xBD0,
        }

        [RegistersDescription("mtl")]
        public enum RegistersMTL : long
        {
            OperatingMode = 0x000,
            InterruptStatus = 0x020,
            TxQueueOperating = 0x100,
            TxQueueUnderflow = 0x104,
            TxQueueDebug = 0x108,
            QueueInterruptControlStatus = 0x12C,
            RxQueueOperatingMode = 0x130,
            RxQueueMissedPacketAndOverflowCounter = 0x134,
            RxQueueDebug = 0x138,
        }

        [RegistersDescription("dma")]
        public enum RegistersDMA : long
        {
            DMAMode = 0x000,
            SystemBusMode = 0x004,
            InterruptStatus = 0x008,
            DebugStatus0 = 0x00C,
            DebugStatus1 = 0x010,
            DebugStatus2 = 0x014,
        }

        public enum RegistersDMAChannel : long
        {
            Control = 0x00,
            TransmitControl = 0x04,
            ReceiveControl = 0x08,
            TxDescriptorListAddress = 0x14,
            RxDescriptorListAddress = 0x1C,
            TxDescriptorTailPointer = 0x20,
            RxDescriptorTailPointer = 0x28,
            TxDescriptorRingLength = 0x2C,
            RxDescriptorRingLength = 0x30,
            InterruptEnable = 0x34,
            RxInterruptWatchdogTimer = 0x38,
            CurrentApplicationTransmitDescriptor = 0x44,
            CurrentApplicationReceiveDescriptor = 0x4C,
            CurrentApplicationTransmitBuffer = 0x54,
            CurrentApplicationReceiveBuffer = 0x5C,
            Status = 0x60,
            MissedFrameCount = 0x6C,
        }

        public enum RegisterSourceAddressOperation : byte
        {
            MACAddressFromInputSignals  = 0b000,
            MACAddressRegisterReserved1 = 0b001,
            MACAddressRegister0Insert   = 0b010,
            MACAddressRegister0Replace  = 0b011,
            MACAddressRegisterReserved2 = 0b100,
            MACAddressRegisterReserved3 = 0b101,
            MACAddressRegister1Insert   = 0b110,
            MACAddressRegister1Replace  = 0b111,
        }

        protected enum DMATxProcessState
        {
            Stopped             = 0b000, // Reset or Stop Receive Command issued
            FetchingDescriptor  = 0b001, // Fetching Tx Transfer Descriptor
            WaitingForStatus    = 0b010, // Waiting for status
            FetchingBufferData  = 0b011, // Reading Data from system memory buffer and queuing it to the Tx buffer (Tx FIFO)
            TimestampWriteState = 0b100, // Timestamp write state
            // Reserved         = 0b101, // Reserved for future use
            Suspended           = 0b110, // Tx Descriptor Unavailable or Tx Buffer Underflow
            ClosingDescriptor   = 0b111, // Closing Tx Descriptor
        }

        protected enum DMARxProcessState
        {
            Stopped             = 0b000, // Reset or Stop Receive Command issued
            FetchingDescriptor  = 0b001, // Fetching Rx Transfer Descriptor
            // Reserved         = 0b010, // Reserved for future use
            WaitingForPacket    = 0b011, // Waiting for Rx packet
            Suspended           = 0b100, // Rx Descriptor Unavailable
            ClosingDescriptor   = 0b101, // Closing the Rx Descriptor
            TimestampWriteState = 0b110, // Timestamp write state
            WritingBufferData   = 0b111, // Transferring the received packet data from the Rx buffer to the system memory
        }

        private enum MIIOperation : byte
        {
            Write = 0b01,
            PostReadAddressIncrement = 0b10,
            Read = 0b11,
        }

        private enum IpcCounter : int
        {
            IpV4Good,
            IpV4HeaderError,
            IpV4NoPayload,
            IpV4Fragmented,
            IpV4UDPChecksumDisabled,
            IpV6Good,
            IpV6HeaderError,
            IpV6NoPayload,
            UdpGood,
            UdpError,
            TcpGood,
            TcpError,
            IcmpGood,
            IcmpError,
        }

        private enum MTLTxQueueReadControllerStatus
        {
            Idle = 0b00,
            Read = 0b01,
            Waiting = 0b10,
            Flushing = 0b11,
        }
    }
}
