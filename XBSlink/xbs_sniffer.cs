﻿/**
 * Project: XBSlink: A XBox360 & PS3/2 System Link Proxy
 * File name: xbs_sniffer.cs
 *   
 * @author Oliver Seuffert, Copyright (C) 2011.
 */
/* 
 * XBSlink is free software; you can redistribute it and/or modify 
 * it under the terms of the GNU General Public License as published by 
 * the Free Software Foundation; either version 2 of the License, or 
 * (at your option) any later version.
 * 
 * This program is distributed in the hope that it will be useful, but 
 * WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY 
 * or FITNESS FOR A PARTICULAR PURPOSE. See the GNU General Public License 
 * for more details.
 * 
 * You should have received a copy of the GNU General Public License along 
 * with this program; If not, see <http://www.gnu.org/licenses/>
 */

using System;
using System.Collections.Generic;
using System.Text;
using PacketDotNet;
using SharpPcap;
using System.Net.NetworkInformation;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace XBSlink
{
    class xbs_sniffer_statistics
    {
        public static volatile UInt32 packet_count = 0;
        private static Object _locker = new Object();
        public static UInt64 NAT_timeInCode = 0;
        public static UInt32 NAT_callCount = 0;
        public static UInt64 deNAT_timeInCode = 0;
        public static UInt32 deNAT_callCount = 0;
    }

    class xbs_sniffer
    {
        private SharpPcap.LibPcap.LibPcapLiveDevice pdev = null;
        public int readTimeoutMilliseconds = 1000;
        public bool pdev_sniff_additional_broadcast = true;
        public bool pdev_filter_use_special_macs = true;
        public bool pdev_filter_only_forward_special_macs = true;

        private String pdev_filter = "(udp and ((ip host 0.0.0.1) or (dst portrange 3074-3075))) ";
        private String pdev_filter_all_broadcast = "(udp and ((ip host 0.0.0.1) or (dst portrange 3074-3075)) or (ether host FF:FF:FF:FF:FF:FF and ip dst host 255.255.255.255)) ";
        private List<PhysicalAddress> pdev_filter_known_macs_from_remote_nodes = new List<PhysicalAddress>();
        private List<PhysicalAddress> pdev_filter_special_macs = new List<PhysicalAddress>();

        private Thread dispatcher_thread = null;
        private volatile bool exiting = false;

        public static Queue<SharpPcap.RawCapture> packets = new Queue<SharpPcap.RawCapture>();

        private List<int> injected_macs_hash = new List<int>();
        private List<int> sniffed_macs_hash = new List<int>();
        private List<PhysicalAddress> sniffed_macs = new List<PhysicalAddress>();

        private xbs_node_list node_list = null;
        private xbs_nat NAT = null;

        public xbs_sniffer(SharpPcap.LibPcap.LibPcapLiveDevice dev, bool sniff_additional_broadcast, bool use_special_mac_filter, bool only_forward_special_macs, xbs_node_list node_list, xbs_nat NAT)
        {
            this.NAT = NAT;
            this.pdev_sniff_additional_broadcast = sniff_additional_broadcast;
            this.pdev_filter_use_special_macs = use_special_mac_filter;
            this.pdev_filter_only_forward_special_macs = only_forward_special_macs;
            injected_macs_hash.Capacity = 10;
            sniffed_macs_hash.Capacity = 10;
            sniffed_macs.Capacity = 10;

            this.node_list = node_list;

            this.pdev = dev;
            pdev.OnPacketArrival +=
                new PacketArrivalEventHandler(OnPacketArrival);
            pdev.Open(DeviceMode.Promiscuous, readTimeoutMilliseconds);
            setPdevFilter();

            if (System.Environment.OSVersion.Platform == PlatformID.Win32NT && pdev is SharpPcap.WinPcap.WinPcapDevice)
                ((SharpPcap.WinPcap.WinPcapDevice)pdev).MinToCopy = 10;

            xbs_messages.addInfoMessage(" - sniffer created on device " + pdev.Description);

            dispatcher_thread = new Thread(new ThreadStart(dispatcher));
            dispatcher_thread.IsBackground = true;
            dispatcher_thread.Priority = ThreadPriority.AboveNormal;
            dispatcher_thread.Start();
        }

        public void start_capture()
        {
#if DEBUG
            xbs_messages.addInfoMessage(" - start capturing packets");
#endif
            pdev.StartCapture();
        }

        public void stop_capture()
        {
            if (pdev.Started)
            {
                try
                {
                    pdev.StopCapture();
                }
                catch (PcapException)
                { }
            }
        }

        public void close()
        {
            stop_capture();
            if (pdev.Opened)
            {
                try
                {
                    pdev.Close();
                }
                catch (PcapException)
                { }
            }
            exiting = true;
            lock (packets)
                Monitor.PulseAll(packets);
            if (dispatcher_thread.ThreadState != System.Threading.ThreadState.Stopped )
                dispatcher_thread.Join();
        }

        private static void OnPacketArrival(object sender, CaptureEventArgs packet)
        {
            try
            {
                lock (xbs_sniffer.packets)
                {
                    xbs_sniffer.packets.Enqueue(packet.Packet);
                    Monitor.PulseAll(packets);
                }
            }
            catch (InvalidOperationException)
            {
                xbs_messages.addInfoMessage("!! InvalidOperationException in sniffer (OnPacketArrival)!");
                return;
            }
        }

        public void dispatcher()
        {
            xbs_messages.addInfoMessage(" - sniffer dispatcher thread starting...");
            int count = 0;
            RawCapture p = null;

#if !DEBUG
            try
            {
#endif
                // loop dispatcher thread until exiting flag is raised
                while (exiting == false)
                {
                    lock (packets)
                        count = packets.Count;

                    // dispatch all packets in queue
                    while (count > 0 && exiting == false)
                    {
                        lock (packets)
                            p = packets.Dequeue();
                        dispatch_packet(ref p);
                        lock (packets)
                            count = packets.Count;
                    }

                    // goto sleep until new packets arrive
                    if (!exiting)
                        lock (packets)
                            Monitor.Wait(packets);
                }
#if !DEBUG
            }
            catch (Exception ex)
            {
                ExceptionMessage.ShowExceptionDialog("sniffer dispatcher service", ex);
            }
#endif
        }

        public void dispatch_packet(ref RawCapture rawPacket)
        {
            byte[] src_mac = new byte[6];
            byte[] dst_mac = new byte[6];
            byte[] packet_data = rawPacket.Data;

            // copy source and destination MAC addresses from sniffed packet
            Buffer.BlockCopy(rawPacket.Data, 0, dst_mac, 0, 6);
            PhysicalAddress dstMAC = new PhysicalAddress(dst_mac);
            Buffer.BlockCopy(rawPacket.Data, 6, src_mac, 0, 6);
            PhysicalAddress srcMAC = new PhysicalAddress(src_mac);

#if DEBUG
            //xbs_messages.addDebugMessage(" - new ethernet packet from "+srcMAC+" => "+dstMAC);
            Packet p = Packet.ParsePacket(rawPacket.LinkLayerType, rawPacket.Data);
            xbs_messages.addDebugMessage("s> "+p);
#endif

            // if sniffed packet has MAC of packet we injected, discard
            bool is_injected_packet = false;
            lock (injected_macs_hash)
                is_injected_packet = injected_macs_hash.Contains(srcMAC.GetHashCode());
            if (is_injected_packet) 
                return;

            if (NAT.NAT_enabled)
            {
#if DEBUG
                System.Diagnostics.Stopwatch stopWatch = new System.Diagnostics.Stopwatch();
                stopWatch.Start();
#endif
                EthernetPacketType p_type = NAT.deNAT_outgoing_packet_PacketDotNet(ref packet_data, dstMAC, srcMAC);
#if DEBUG
                stopWatch.Stop();
                if (p_type == EthernetPacketType.IpV4)
                {
                    xbs_sniffer_statistics.deNAT_callCount++;
                    if (xbs_sniffer_statistics.deNAT_callCount > 1)
                    {
                        xbs_sniffer_statistics.deNAT_timeInCode += (UInt64)stopWatch.ElapsedTicks;
                        UInt32 average = (UInt32)(xbs_sniffer_statistics.deNAT_timeInCode / (xbs_sniffer_statistics.deNAT_callCount - 1));
                        double average_ms = new TimeSpan(average).TotalMilliseconds;
                        xbs_messages.addDebugMessage("- deNAT time: " + stopWatch.ElapsedTicks + " deNAT count: " + (xbs_sniffer_statistics.deNAT_callCount - 1) + " Total Time: " + xbs_sniffer_statistics.deNAT_timeInCode + "=> " + average + " / " + average_ms + "ms");
                    }
                }
                p = Packet.ParsePacket(rawPacket.LinkLayerType, packet_data);
                xbs_messages.addDebugMessage("i> " + p);
#endif
            }

            // count the sniffed packets from local xboxs
            xbs_sniffer_statistics.packet_count++;

            // find node with destination MAC Address in network and send packet
            node_list.distributeDataPacket(dstMAC, packet_data);

            int srcMac_hash = srcMAC.GetHashCode();
            bool pdevfilter_needs_change = false;
            lock (sniffed_macs_hash)
            {
                if (!sniffed_macs_hash.Contains(srcMac_hash))
                {
                    sniffed_macs_hash.Add(srcMac_hash);
                    lock (sniffed_macs)
                        sniffed_macs.Add(srcMAC);
                    pdevfilter_needs_change = true;
                }
            }
            if (pdevfilter_needs_change)
                setPdevFilter();
        }

        public void injectRemotePacket(ref byte[] data, PhysicalAddress dstMAC, PhysicalAddress srcMAC)
        {
            int srcMac_hash = srcMAC.GetHashCode();
            // collect all injected source MACs. sniffer needs this to filter packets out
            lock (injected_macs_hash)
            {
                if (!injected_macs_hash.Contains(srcMac_hash))
                {
                    injected_macs_hash.Add(srcMac_hash);
                    addMacToKnownMacListFromRemoteNodes(srcMAC);
                }
            }

#if DEBUG
            Packet p = Packet.ParsePacket(LinkLayers.Ethernet, data);
            xbs_messages.addDebugMessage("i> "+p);
#endif

            if (NAT.NAT_enabled)
            {
#if DEBUG
                System.Diagnostics.Stopwatch stopWatch = new System.Diagnostics.Stopwatch();
                stopWatch.Start();
#endif
                EthernetPacketType p_type = NAT.NAT_incoming_packet_PacketDotNet(ref data, dstMAC, srcMAC);
#if DEBUG
                stopWatch.Stop();
                if (p_type == EthernetPacketType.IpV4)
                {
                    xbs_sniffer_statistics.NAT_callCount++;
                    if (xbs_sniffer_statistics.NAT_callCount > 1)
                    {
                        xbs_sniffer_statistics.NAT_timeInCode += (UInt64)stopWatch.ElapsedTicks;
                        UInt32 average = (UInt32)(xbs_sniffer_statistics.NAT_timeInCode / (xbs_sniffer_statistics.NAT_callCount - 1));
                        double average_ms = new TimeSpan(average).TotalMilliseconds;
                        xbs_messages.addDebugMessage("- NAT time: " + stopWatch.ElapsedTicks+"t/"+stopWatch.ElapsedMilliseconds + "ms | NAT count: " + (xbs_sniffer_statistics.NAT_callCount - 1) + " Total Time: " + xbs_sniffer_statistics.NAT_timeInCode + "t=> Average " + average + "t / " + average_ms+"ms");
                    }
                }
                p = Packet.ParsePacket(LinkLayers.Ethernet, data);
                xbs_messages.addDebugMessage("i> " + p);
#endif
            }

            // inject the packet 
            try
            {
                pdev.SendPacket(data, data.Length);
            }
            catch (PcapException pex)
            {
                xbs_messages.addInfoMessage("!! error while injecting packet from "+srcMAC+" to "+dstMAC+" ("+data.Length+") : "+pex.Message);
            }
            catch (ArgumentException aex)
            {
                xbs_messages.addInfoMessage("!! error while injecting packet from " + srcMAC + " to " + dstMAC + " (" + data.Length + ") : " + aex.Message);
            }
        }

        public PhysicalAddress[] getSniffedMACs()
        {
            PhysicalAddress[] mac_array;
            lock (sniffed_macs)
            {
                mac_array = new PhysicalAddress[ sniffed_macs.Count ];
                sniffed_macs.CopyTo(mac_array);
            }
            return mac_array;
        }

        public void clearKnownMACsFromRemoteNodes()
        {
            lock (pdev_filter_known_macs_from_remote_nodes)
                pdev_filter_known_macs_from_remote_nodes.Clear();
        }

        public void addMacToKnownMacListFromRemoteNodes(PhysicalAddress mac)
        {
            lock (pdev_filter_known_macs_from_remote_nodes)
                if (!pdev_filter_known_macs_from_remote_nodes.Contains(mac))
                    pdev_filter_known_macs_from_remote_nodes.Add(mac);
            setPdevFilter();
        }

        public void addMacToSpecialPacketFilter(PhysicalAddress mac)
        {
            lock (pdev_filter_special_macs)
                if (!pdev_filter_special_macs.Contains(mac))
                    pdev_filter_special_macs.Add(mac);
            setPdevFilter();
        }

        public void removeMacFromSpecialPacketFilter(PhysicalAddress mac)
        {
            lock (pdev_filter_special_macs)
            {
                if (pdev_filter_special_macs.Contains(mac))
                    pdev_filter_special_macs.Remove(mac);
            }
            setPdevFilter();
        }

        public static String PhysicalAddressToString(PhysicalAddress mac)
        {
            return BitConverter.ToString(mac.GetAddressBytes()).Replace('-',':');
        }

        public void setPdevFilter()
        {
            String filter_known_macs_from_remote_nodes = String.Empty;
            String filter_exclude_injected_packets = String.Empty;
            String filter_special_macs = String.Empty;
            String filter_discovered_devices = String.Empty;
            lock (pdev_filter_known_macs_from_remote_nodes)
            {
                if (pdev_filter_known_macs_from_remote_nodes.Count > 0)
                {
                    // we want all packets send to MACs we know of from other XBSlink nodes
                    filter_known_macs_from_remote_nodes = " or ( ether dst " + String.Join(" or ether dst ", pdev_filter_known_macs_from_remote_nodes.ConvertAll<string>(delegate(PhysicalAddress pa) { return PhysicalAddressToString(pa); }).ToArray()) + " )";
                    // we do NOT want packets injected by us, send from other other nodes to out network
                    filter_exclude_injected_packets = " and not ( ether src " + String.Join(" or ether src ", pdev_filter_known_macs_from_remote_nodes.ConvertAll<string>(delegate(PhysicalAddress pa) { return PhysicalAddressToString(pa); }).ToArray()) + " )";
                }
            }
            if (pdev_filter_use_special_macs)
            {
                lock (pdev_filter_special_macs)
                {
                    if (pdev_filter_special_macs.Count > 0)
                    {
                        filter_special_macs = (pdev_filter_only_forward_special_macs == false) ? " or ether src " : "ether src ";
                        filter_special_macs += String.Join(" or ether src ", pdev_filter_special_macs.ConvertAll<string>(delegate(PhysicalAddress pa) { return PhysicalAddressToString(pa); }).ToArray());
                    }
                }
            }
            lock (sniffed_macs)
            {
                if (sniffed_macs.Count > 0)
                {
                    filter_discovered_devices = " or ( ether src " + String.Join(" or ether src ", sniffed_macs.ConvertAll<string>(delegate(PhysicalAddress pa) { return PhysicalAddressToString(pa); }).ToArray()) + " )";
                }
            }
            try
            {
                String f = (pdev_sniff_additional_broadcast ? pdev_filter_all_broadcast : pdev_filter) + filter_special_macs + filter_known_macs_from_remote_nodes + filter_discovered_devices + filter_exclude_injected_packets;
                if (pdev_filter_use_special_macs && pdev_filter_only_forward_special_macs && filter_special_macs.Length>0)
                    f = filter_special_macs;
#if DEBUG
                xbs_messages.addInfoMessage("- pdev filter: " + f);
#endif
                pdev.Filter = f;
            }
            catch (PcapException)
            {
                xbs_messages.addInfoMessage("!! - ERROR setting pdev filter");
            }
        }

        public void setSpecialMacPacketFilter(List<PhysicalAddress> mac_list)
        {
            lock (pdev_filter_special_macs)
                pdev_filter_special_macs = mac_list;
            setPdevFilter();
        }

        public static xbs_sniffer getInstance()
        {
            return (FormMain.sniffer != null) ? FormMain.sniffer : xbs_console_app.sniffer;
        }
    }
}
