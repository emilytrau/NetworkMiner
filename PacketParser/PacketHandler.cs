//  Copyright: Erik Hjelmvik, NETRESEC
//
//  NetworkMiner is free software; you can redistribute it and/or modify it
//  under the terms of the GNU General Public License
//

using System;
using System.Net;
using System.Collections.Generic;
using System.Text;
using System.IO;
using PacketParser.FileTransfer;

namespace PacketParser {

    //internal delegate void NewNetworkHostHandler(NetworkHost host);
    public delegate void AnomalyEventHandler(object sender, Events.AnomalyEventArgs ae);
    public delegate void ParameterEventHandler(object sender, Events.ParametersEventArgs pe);
    public delegate void NetworkHostEventHandler(object sender, Events.NetworkHostEventArgs he);
    public delegate void HttpClientEventHandler(object sender, Events.HttpClientEventArgs he);
    public delegate void DnsRecordEventHandler(object sender, Events.DnsRecordEventArgs de);
    public delegate void BufferUsageEventHandler(object sender, Events.BufferUsageEventArgs be);
    public delegate void FrameEventHandler(object sender, Events.FrameEventArgs fe);
    public delegate void CleartextWordsEventHandler(object sender, Events.CleartextWordsEventArgs ce);
    public delegate void FileEventHandler(object sender, Events.FileEventArgs fe);
    public delegate void KeywordEventHandler(object sender, Events.KeywordEventArgs ke);
    public delegate void CredentialEventHandler(object sender, Events.CredentialEventArgs ce);
    public delegate void SessionEventHandler(object sender, Events.SessionEventArgs se);
    public delegate void MessageEventHandler(object sender, Events.MessageEventArgs me);



    public class PacketHandler {
        internal static PopularityList<string, List<Packets.IPv4Packet>> Ipv4Fragments = new PopularityList<string, List<Packets.IPv4Packet>>(1024);
        private long nFramesReceived, nBytesReceived;
        private CleartextDictionary.WordDictionary dictionary;

        private PopularityList<int, NetworkTcpSession> networkTcpSessionList;
        private SortedList<string, NetworkCredential> credentialList;
        //TODO: Add PopularityList of type <FileSegmentAssembler>
        private Func<DateTime, string> toCustomTimeZoneStringFunction;

        private System.Collections.Concurrent.BlockingCollection<SharedUtils.Pcap.PacketReceivedEventArgs> receivedPacketsQueue_BC;
        public const int RECEIVED_PACKETS_QUEUE_MAX_SIZE = 16000;

        private readonly System.Collections.Concurrent.BlockingCollection<Frame> framesToParseQueue;
        private long framesToParseQueuedByteCount;

        public long FramesToParseQueuedByteCount {
            get {
                return System.Threading.Interlocked.Read(ref this.framesToParseQueuedByteCount);
            }
        }

        private byte[][] keywordList;
        private int cleartextSearchModeSelectedIndex;

        private int? lastBufferUsagePercent;

        //Packet handlers
        private List<PacketHandlers.IPacketHandler> nonIpPacketHandlerList;//protocols in frames without IP packets
        private List<PacketHandlers.IPacketHandler> packetHandlerList;
        private List<PacketHandlers.ITcpSessionPacketHandler> tcpSessionPacketHandlerList;

        //Threads
        private System.Threading.Thread packetQueueConsumerThread;
        private System.Threading.Thread frameQueueConsumerThread;

        private object insufficientWritePermissionsLock = new object();
        private bool useRelativePathIfAvailable = true;

        internal readonly string FingerprintsPath;

        public event AnomalyEventHandler AnomalyDetected;
        public event ParameterEventHandler ParametersDetected;
        public event NetworkHostEventHandler NetworkHostDetected;
        public event HttpClientEventHandler HttpTransactionDetected;
        public event DnsRecordEventHandler DnsRecordDetected;
        public event BufferUsageEventHandler BufferUsageChanged;
        public event FrameEventHandler FrameDetected;
        public event CleartextWordsEventHandler CleartextWordsDetected;
        public event FileEventHandler FileReconstructed;
        public event KeywordEventHandler KeywordDetected;
        public event CredentialEventHandler CredentialDetected;
        public event SessionEventHandler SessionDetected;
        public event MessageEventHandler MessageDetected;
        public event FileTransfer.FileStreamAssembler.FileReconsructedEventHandler MessageAttachmentDetected;
        public event Action<AudioStream> AudioDetected;
        public event Action<System.Net.IPAddress, ushort, System.Net.IPAddress, ushort, string, string, string> VoipCallDetected;
        public event UnhandledExceptionEventHandler UnhandledException;
        public event Action<string> InsufficientWritePermissionsDetected;

        public byte[][] KeywordList { set { this.keywordList = value; } }
        public int CleartextSearchModeSelectedIndex { set { this.cleartextSearchModeSelectedIndex = value; } }

        public CleartextDictionary.WordDictionary Dictionary { set { this.dictionary = value; } }
        public List<Fingerprints.IOsFingerprinter> OsFingerprintCollectionList { get; }
        public NetworkHostList NetworkHostList { get; }
        public FileTransfer.FileStreamAssemblerList FileStreamAssemblerList { get; }
        public PopularityList<(NetworkTcpSession tcpSession, bool clientToServer), ITcpStreamAssembler> TcpStreamAssemblerList { get; }
        public int PacketsInQueue { get { return this.receivedPacketsQueue_BC.Count; } }
        public int FramesInQueue { get { return this.framesToParseQueue.Count; } }

        public List<FileTransfer.ReconstructedFile> ReconstructedFileList { get; }
        public ISessionProtocolFinderFactory ProtocolFinderFactory { get; set; }

        public IPacketFilter InputFilter { get; set; } = null;

        public PacketHandlers.IHttpPacketHandler ExtraHttpPacketHandler { get; set; } = null;

        public string OutputDirectory { get; }
        public bool DefangExecutableFiles { get; set; }
        public bool ExtractPartialDownloads { get; set; } = true;
        public Dictionary<FileTransfer.FileStreamTypes, FileTransfer.IFileCarver> FileCarvers {
            get;
            set;
        }

        public void ResetCapturedData(bool removeExtractedFilesFromDisk) {
            lock (this.receivedPacketsQueue_BC) {
                lock (this.NetworkHostList)
                    this.NetworkHostList.Clear();
                this.nFramesReceived = 0;
                this.nBytesReceived = 0;
                this.networkTcpSessionList.Clear();
                lock (Ipv4Fragments)
                    Ipv4Fragments.Clear();
                lock (this.ReconstructedFileList)
                    this.ReconstructedFileList.Clear();
                lock (this.credentialList)
                    this.credentialList.Clear();
                this.lastBufferUsagePercent = null;

                foreach (PacketHandlers.IPacketHandler packetHandler in this.packetHandlerList)
                    packetHandler.Reset();
                foreach (PacketHandlers.ITcpSessionPacketHandler packetHandler in this.tcpSessionPacketHandlerList)
                    packetHandler.Reset();
                this.FileStreamAssemblerList.Clear(removeExtractedFilesFromDisk);
                this.TcpStreamAssemblerList.Clear();
                this.ProtocolFinderFactory.Reset();

                while (this.receivedPacketsQueue_BC.TryTake(out var packetReceivedEventArgs)) { }

            }
            if (this.ExtraHttpPacketHandler != null)
                this.ExtraHttpPacketHandler.Reset();
        }

#if DEBUG

        public void Disable() {
            this.AnomalyDetected += (o, k) => { System.Diagnostics.Debugger.Break(); throw new ObjectDisposedException("This PacketHandler is disabled, use a different one!"); };
            this.ParametersDetected += (o, k) => { System.Diagnostics.Debugger.Break(); throw new ObjectDisposedException("This PacketHandler is disabled, use a different one!"); };
            this.NetworkHostDetected += (o, k) => { System.Diagnostics.Debugger.Break(); throw new ObjectDisposedException("This PacketHandler is disabled, use a different one!"); };
            this.HttpTransactionDetected += (o, k) => { System.Diagnostics.Debugger.Break(); throw new ObjectDisposedException("This PacketHandler is disabled, use a different one!"); };
            this.DnsRecordDetected += (o, k) => { System.Diagnostics.Debugger.Break(); throw new ObjectDisposedException("This PacketHandler is disabled, use a different one!"); };
            this.BufferUsageChanged += (o, k) => { System.Diagnostics.Debugger.Break(); throw new ObjectDisposedException("This PacketHandler is disabled, use a different one!"); };
            this.FrameDetected += (o, k) => { System.Diagnostics.Debugger.Break(); throw new ObjectDisposedException("This PacketHandler is disabled, use a different one!"); };
            this.CleartextWordsDetected += (o, k) => { System.Diagnostics.Debugger.Break(); throw new ObjectDisposedException("This PacketHandler is disabled, use a different one!"); };
            this.FileReconstructed += (o, k) => { System.Diagnostics.Debugger.Break(); throw new ObjectDisposedException("This PacketHandler is disabled, use a different one!"); };
            this.KeywordDetected += (o, k) => { System.Diagnostics.Debugger.Break(); throw new ObjectDisposedException("This PacketHandler is disabled, use a different one!"); };
            this.CredentialDetected += (o, k) => { System.Diagnostics.Debugger.Break(); throw new ObjectDisposedException("This PacketHandler is disabled, use a different one!"); };
            this.SessionDetected += (o, k) => { System.Diagnostics.Debugger.Break(); throw new ObjectDisposedException("This PacketHandler is disabled, use a different one!"); };
            this.MessageDetected += (o, k) => { System.Diagnostics.Debugger.Break(); throw new ObjectDisposedException("This PacketHandler is disabled, use a different one!"); };
            this.MessageAttachmentDetected += (o, k) => { System.Diagnostics.Debugger.Break(); throw new ObjectDisposedException("This PacketHandler is disabled, use a different one!"); };
        }
#endif

        public PacketHandler(string applicationExecutablePath, string outputPath, List<Fingerprints.IOsFingerprinter> preloadedFingerprints, bool ignoreMissingFingerprintFiles, Func<DateTime, string> toCustomTimeZoneStringFunction, bool useRelativePathIfAvailable, bool verifyX509Certificates, byte vncMaxFPS) {
            this.useRelativePathIfAvailable = useRelativePathIfAvailable;
            this.toCustomTimeZoneStringFunction = toCustomTimeZoneStringFunction;
            this.ProtocolFinderFactory = new PortProtocolFinderFactory(this);

            this.NetworkHostList = new NetworkHostList();
            this.nFramesReceived = 0;
            this.nBytesReceived = 0;
            this.dictionary = new CleartextDictionary.WordDictionary();
            this.lastBufferUsagePercent = null;

            this.receivedPacketsQueue_BC = new System.Collections.Concurrent.BlockingCollection<SharedUtils.Pcap.PacketReceivedEventArgs>(RECEIVED_PACKETS_QUEUE_MAX_SIZE);

            this.framesToParseQueue = new System.Collections.Concurrent.BlockingCollection<Frame>(RECEIVED_PACKETS_QUEUE_MAX_SIZE);

            this.packetQueueConsumerThread = new System.Threading.Thread(new System.Threading.ThreadStart(delegate () { this.CreateFramesFromPacketsInPacketQueue(); }));
            this.frameQueueConsumerThread = new System.Threading.Thread(new System.Threading.ThreadStart(delegate () { this.ParseFramesInFrameQueue(); }));

            string applicationDirectory = Path.GetDirectoryName(applicationExecutablePath) + System.IO.Path.DirectorySeparatorChar;
            this.FingerprintsPath = applicationDirectory + "Fingerprints" + System.IO.Path.DirectorySeparatorChar;

            if (!outputPath.EndsWith(System.IO.Path.DirectorySeparatorChar.ToString()))
                outputPath += System.IO.Path.DirectorySeparatorChar.ToString();
            this.OutputDirectory = Path.GetDirectoryName(outputPath) + System.IO.Path.DirectorySeparatorChar;
            this.OsFingerprintCollectionList = new List<Fingerprints.IOsFingerprinter>();
            if (preloadedFingerprints != null)
                this.OsFingerprintCollectionList.AddRange(preloadedFingerprints);
            this.FileCarvers = new Dictionary<FileTransfer.FileStreamTypes, FileTransfer.IFileCarver>();

            //the ettercap fingerprints aren't needed
            try {
                OsFingerprintCollectionList.Add(new Fingerprints.EttarcapOsFingerprintCollection(this.FingerprintsPath + "etter.finger.os"));//, NetworkMiner.Fingerprints.EttarcapOsFingerprintCollection.OsFingerprintFileFormat.Ettercap)
            }
            catch (FileNotFoundException) { }
            try {
                //Check CERT NetSA p0f database https://tools.netsa.cert.org/confluence/display/tt/p0f+fingerprints
                string netsaP0fFile = this.FingerprintsPath + "p0f.fp.netsa";
                if (System.IO.File.Exists(netsaP0fFile))
                    this.OsFingerprintCollectionList.Add(new Fingerprints.P0fOsFingerprintCollection(netsaP0fFile, applicationDirectory + System.IO.Path.DirectorySeparatorChar + "Fingerprints" + System.IO.Path.DirectorySeparatorChar + "p0fa.fp", "p0f (NetSA)", 0.4));
                else
                    this.OsFingerprintCollectionList.Add(new Fingerprints.P0fOsFingerprintCollection(this.FingerprintsPath + "p0f.fp", applicationDirectory + System.IO.Path.DirectorySeparatorChar + "Fingerprints" + System.IO.Path.DirectorySeparatorChar + "p0fa.fp"));
                this.OsFingerprintCollectionList.Add(new Fingerprints.SatoriDhcpOsFingerprinter(this.FingerprintsPath + "dhcp.xml"));
                this.OsFingerprintCollectionList.Add(new Fingerprints.SatoriTcpOsFingerprinter(this.FingerprintsPath + "tcp.xml"));
            }
            catch (FileNotFoundException e) {
                if (!ignoreMissingFingerprintFiles)
                    throw;//re-throw the exception
            }
            this.networkTcpSessionList = new PopularityList<int, NetworkTcpSession>(200);
            this.networkTcpSessionList.PopularityLost += (key, session) => session.Close();
            this.FileStreamAssemblerList = new FileTransfer.FileStreamAssemblerList(this, 100, this.OutputDirectory + PacketParser.FileTransfer.FileStreamAssembler.ASSMEBLED_FILES_DIRECTORY + System.IO.Path.DirectorySeparatorChar);
            this.FileStreamAssemblerList.PopularityLost += this.FileStreamAssemblerList_PopularityLost;
            this.TcpStreamAssemblerList = new PopularityList<(NetworkTcpSession tcpSession, bool clientToServer), ITcpStreamAssembler>(100);
            this.TcpStreamAssemblerList.PopularityLost += (key, assembler) => assembler.Clear();
            this.ReconstructedFileList = new List<FileTransfer.ReconstructedFile>();
            this.credentialList = new SortedList<string, NetworkCredential>();

            this.nonIpPacketHandlerList = new List<PacketHandlers.IPacketHandler>();
            this.packetHandlerList = new List<PacketHandlers.IPacketHandler>();
            this.tcpSessionPacketHandlerList = new List<PacketHandlers.ITcpSessionPacketHandler>();
            //packet handlers should be entered into the handlerList in the order that packets should be processed
            this.nonIpPacketHandlerList.Add(new PacketHandlers.HpSwitchProtocolPacketHandler(this));
            PacketHandlers.DnsPacketHandler dnsPacketHandler = new PacketHandlers.DnsPacketHandler(this);
            this.packetHandlerList.Add(dnsPacketHandler);
            this.packetHandlerList.Add(new PacketHandlers.TftpPacketHandler(this));
            this.packetHandlerList.Add(new PacketHandlers.NetBiosDatagramServicePacketHandler(this));
            this.packetHandlerList.Add(new PacketHandlers.NetBiosNameServicePacketHandler(this));
            this.packetHandlerList.Add(new PacketHandlers.UpnpPacketHandler(this));
            this.packetHandlerList.Add(new PacketHandlers.DhcpPacketHandler(this));
            this.packetHandlerList.Add(new PacketHandlers.KerberosPacketHandler(this));
            this.packetHandlerList.Add(new PacketHandlers.RtpPacketHandler(this));
            this.packetHandlerList.Add(new PacketHandlers.SipPacketHandler(this, UdpPortProtocolFinder.PipiInstance));
            this.packetHandlerList.Add(new PacketHandlers.SnmpPacketHandler(this));
            this.packetHandlerList.Add(new PacketHandlers.SyslogPacketHandler(this));
            this.packetHandlerList.Add(new PacketHandlers.CifsBrowserPacketHandler(this));
            this.tcpSessionPacketHandlerList.Add(dnsPacketHandler);
            this.tcpSessionPacketHandlerList.Add(new PacketHandlers.BackConnectPacketHandler(this, vncMaxFPS));
            this.tcpSessionPacketHandlerList.Add(new PacketHandlers.FtpPacketHandler(this));
            this.tcpSessionPacketHandlerList.Add(new PacketHandlers.HttpPacketHandler(this));
            this.tcpSessionPacketHandlerList.Add(new PacketHandlers.Http2PacketHandler(this, dnsPacketHandler));
            this.tcpSessionPacketHandlerList.Add(new PacketHandlers.ImapPacketHandler(this));
            this.tcpSessionPacketHandlerList.Add(new PacketHandlers.IrcPacketHandler(this));
            this.tcpSessionPacketHandlerList.Add(new PacketHandlers.KerberosPacketHandler(this));
            this.tcpSessionPacketHandlerList.Add(new PacketHandlers.LpdPacketHandler(this));
            this.tcpSessionPacketHandlerList.Add(new PacketHandlers.NetBiosSessionServicePacketHandler(this));
            this.tcpSessionPacketHandlerList.Add(new PacketHandlers.NjRatPacketHandler(this));
            this.tcpSessionPacketHandlerList.Add(new PacketHandlers.NtlmSspPacketHandler(this));
            this.tcpSessionPacketHandlerList.Add(new PacketHandlers.Pop3PacketHandler(this));
            this.tcpSessionPacketHandlerList.Add(new PacketHandlers.RfbPacketHandler(this, vncMaxFPS));
            this.tcpSessionPacketHandlerList.Add(new PacketHandlers.SipPacketHandler(this));
            this.tcpSessionPacketHandlerList.Add(new PacketHandlers.SmbCommandPacketHandler(this));
            this.tcpSessionPacketHandlerList.Add(new PacketHandlers.Smb2PacketHandler(this));
            this.tcpSessionPacketHandlerList.Add(new PacketHandlers.SmtpPacketHandler(this));
            this.tcpSessionPacketHandlerList.Add(new PacketHandlers.SocksPacketHandler(this));
            this.tcpSessionPacketHandlerList.Add(new PacketHandlers.SpotifyKeyExchangePacketHandler(this));
            this.tcpSessionPacketHandlerList.Add(new PacketHandlers.SshPacketHandler(this));
            this.tcpSessionPacketHandlerList.Add(new PacketHandlers.TabularDataStreamPacketHandler(this));
            this.tcpSessionPacketHandlerList.Add(new PacketHandlers.TlsRecordPacketHandler(this, verifyX509Certificates));
            this.tcpSessionPacketHandlerList.Add(new PacketHandlers.OscarFileTransferPacketHandler(this));
            this.tcpSessionPacketHandlerList.Add(new PacketHandlers.OscarPacketHandler(this));
            this.tcpSessionPacketHandlerList.Add(new PacketHandlers.IEC_104_PacketHandler(this));
            this.tcpSessionPacketHandlerList.Add(new PacketHandlers.ModbusTcpPacketHandler(this));
            this.tcpSessionPacketHandlerList.Add(new PacketHandlers.RdpPacketHandler(this));
            this.tcpSessionPacketHandlerList.Add(new PacketHandlers.MeterpreterPacketHandler(this));
            this.tcpSessionPacketHandlerList.Add(new PacketHandlers.GenericShimPacketHandler<Packets.OpenFlowPacket>(this, ApplicationLayerProtocol.OpenFlow));
            this.tcpSessionPacketHandlerList.Add(new PacketHandlers.GenericShimPacketHandler<Packets.TpktPacket>(this, ApplicationLayerProtocol.TPKT));
            this.tcpSessionPacketHandlerList.Add(new PacketHandlers.UnusedTcpSessionProtocolsHandler(this));//this one is needed in order to release packets from the TCP reassembly if they are complete.

            this.keywordList = new byte[0][];
        }

        private void FileStreamAssemblerList_PopularityLost(string key, FileTransfer.FileStreamAssembler value) {
            if (value is PacketParser.FileTransfer.AuFileAssembler auFile) {
                auFile.FinishAssembling();
            }
        }

        public void SetUndecidedProtocolsToUnknown() {
            //Set undecided protocols to Unknown
            foreach (NetworkTcpSession session in this.networkTcpSessionList.GetValueEnumerator()) {
                if (session.ProtocolFinder.GetConfirmedApplicationLayerProtocol() == ApplicationLayerProtocol.Unknown)
                    session.ProtocolFinder.SetConfirmedApplicationLayerProtocol(ApplicationLayerProtocol.Unknown, false);
            }
        }


        public void StartBackgroundThreads() {

            //packetHandlerThread.Start();

            this.packetQueueConsumerThread.Start();
            this.frameQueueConsumerThread.Start();
        }

        public void AbortBackgroundThreads() {
            this.packetQueueConsumerThread.Abort();
            this.frameQueueConsumerThread.Abort();
        }

        internal virtual void OnInsufficientWritePermissionsDetected(string path) {
            this.OnAnomalyDetected("User does not have write permissions to " + path);
            if (this.InsufficientWritePermissionsDetected != null) {
                lock (this.insufficientWritePermissionsLock) {
                    this.InsufficientWritePermissionsDetected?.Invoke(path);
                    this.InsufficientWritePermissionsDetected = null;
                }
            }
        }

        // http://msdn.microsoft.com/en-us/library/aa645739(VS.71).aspx
        public virtual void OnAnomalyDetected(Events.AnomalyEventArgs ae) {
            SharedUtils.Logger.Log("Anomaly detected: " + ae.Message, SharedUtils.Logger.EventLogEntryType.Information);
            AnomalyDetected?.Invoke(this, ae);
        }
        internal virtual void OnAnomalyDetected(string anomalyMessage) {

            OnAnomalyDetected(anomalyMessage, DateTime.Now);
        }
        internal virtual void OnAnomalyDetected(string anomalyMessage, DateTime anomalyTimestamp) {
            this.OnAnomalyDetected(new Events.AnomalyEventArgs(anomalyMessage, anomalyTimestamp));
        }
        internal virtual void OnParametersDetected(Events.ParametersEventArgs pe) {
            ParametersDetected?.Invoke(this, pe);
        }
        internal virtual void OnNetworkHostDetected(Events.NetworkHostEventArgs he) {
            NetworkHostDetected?.Invoke(this, he);
        }
        public virtual void OnHttpClientDetected(Events.HttpClientEventArgs he) {
            HttpTransactionDetected?.Invoke(this, he);
        }
        internal virtual void OnDnsRecordDetected(Events.DnsRecordEventArgs de) {
            DnsRecordDetected?.Invoke(this, de);
        }
        internal virtual void OnBufferUsageChanged(Events.BufferUsageEventArgs be) {
            BufferUsageChanged?.Invoke(this, be);
        }
        internal virtual void OnFrameDetected(Events.FrameEventArgs fe) {
            FrameDetected?.Invoke(this, fe);
        }
        internal virtual void OnCleartextWordsDetected(Events.CleartextWordsEventArgs ce) {
            CleartextWordsDetected?.Invoke(this, ce);
        }
        internal virtual void OnFileReconstructed(Events.FileEventArgs fe) {
            FileReconstructed?.Invoke(this, fe);
        }
        internal virtual void OnKeywordDetected(Events.KeywordEventArgs ke) {
            KeywordDetected?.Invoke(this, ke);
        }

        //this one should only be called by PacketParser.PachetHandler so that credentials can be filtered later on
        private void OnCredentialDetected(Events.CredentialEventArgs ce) {
            CredentialDetected?.Invoke(this, ce);
        }
        internal virtual void OnSessionDetected(Events.SessionEventArgs se) {
            this.SessionDetected?.Invoke(this, se);
        }


        internal virtual void OnMessageDetected(Events.MessageEventArgs me) {
            this.MessageDetected?.Invoke(this, me);
        }
        internal virtual void OnMessageAttachmentDetected(string messageId, PacketParser.FileTransfer.ReconstructedFile file) {
            this.MessageAttachmentDetected?.Invoke(messageId, file);
        }
        internal virtual void OnAudioDetected(AudioStream audioStream) {
            this.AudioDetected?.Invoke(audioStream);
        }
        //internal virtual void OnVoipCallDetected(FiveTuple fiveTuple, string from, string to) {
        internal virtual void OnVoipCallDetected(System.Net.IPAddress ipA, ushort portA, System.Net.IPAddress ipB, ushort portB, string callId, string from, string to) {
            this.VoipCallDetected?.Invoke(ipA, portA, ipB, portB, callId, from, to);
        }

        private IEnumerable<string> GetCleartextWords(Packets.AbstractPacket packet) {
            return GetCleartextWords(packet.ParentFrame.Data, packet.PacketStartIndex, packet.PacketEndIndex);
        }
        private IEnumerable<string> GetCleartextWords(byte[] data) {
            return GetCleartextWords(data, 0, data.Length - 1);
        }
        /// <summary>
        /// Displays words in cleartext that exists in the provided data range
        /// </summary>
        /// <param name="data">Array of data</param>
        /// <param name="startIndex">Index in array to start search at</param>
        /// <param name="endIndex">Index in array to en search in</param>
        /// <returns></returns>
        private IEnumerable<string> GetCleartextWords(byte[] data, int startIndex, int endIndex) {
            if (this.dictionary.WordCount > 0) {
                StringBuilder sb = null;//new StringBuilder();
                for (int i = startIndex; i <= endIndex; i++) {
                    if (dictionary.IsLetter(data[i])) {
                        if (sb == null)
                            sb = new StringBuilder(Convert.ToString((char)data[i]));
                        else
                            sb.Append((char)data[i]);
                    }
                    else {
                        if (sb != null) {
                            if (dictionary.HasWord(sb.ToString()))
                                yield return sb.ToString();
                            sb = null;
                        }
                    }
                }
                if (sb != null && dictionary.HasWord(sb.ToString()))
                    yield return sb.ToString();
            }
        }


        /// <summary>
        /// Callback method to receive packets from a sniffer
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="packet"></param>
        public bool TryEnqueueReceivedPacket(object sender, SharedUtils.Pcap.PacketReceivedEventArgs packet) {
            if (this.receivedPacketsQueue_BC.Count < RECEIVED_PACKETS_QUEUE_MAX_SIZE) {

                this.receivedPacketsQueue_BC.Add(packet);
                //this.receivedPacketsQueue.Enqueue(packet);

                this.OnBufferUsageChanged(new Events.BufferUsageEventArgs((this.receivedPacketsQueue_BC.Count * 100) / RECEIVED_PACKETS_QUEUE_MAX_SIZE));
                //this.parentForm.SetBufferUsagePercent((this.receivedPacketsQueue.Count*100)/RECEIVED_PACKETS_QUEUE_MAX_SIZE);
                return true;
            }
            else {
                this.OnAnomalyDetected("Packet dropped");
                //this.parentForm.ShowError("Packet dropped");
                return false;
            }
        }

        private void UpdateBufferUsagePercent() {
            int usage = Math.Min(Math.Max(this.receivedPacketsQueue_BC.Count, this.framesToParseQueue.Count), RECEIVED_PACKETS_QUEUE_MAX_SIZE);
            int percent = (usage * 100) / RECEIVED_PACKETS_QUEUE_MAX_SIZE;
            if (this.lastBufferUsagePercent == null || percent != lastBufferUsagePercent.Value) {
                this.lastBufferUsagePercent = percent;
                this.OnBufferUsageChanged(new Events.BufferUsageEventArgs((usage * 100) / RECEIVED_PACKETS_QUEUE_MAX_SIZE));
            }
        }


        internal void CreateFramesFromPacketsInPacketQueue() {
            try {
                while (true) {

                    Frame frame = this.GetFrame(this.receivedPacketsQueue_BC.Take());//Take() will block until there is a packet in the queue
                    if (frame != null)
                        this.AddFrameToFrameParsingQueue(frame);

                }
            }
            catch (System.Threading.ThreadAbortException) {
                throw;
            }
#if !DEBUG
            catch(Exception e) {
                SharedUtils.Logger.Log("Error creating frame from packet. " + e.ToString(), SharedUtils.Logger.EventLogEntryType.Warning);
                if (this.UnhandledException == null)
                    throw;
                else
                    this.UnhandledException(AppDomain.CurrentDomain, new UnhandledExceptionEventArgs(e, true));
            }
#endif
        }


        public void AddFrameToFrameParsingQueue(Frame frame) {
            if (frame != null) {

                this.framesToParseQueue.Add(frame);
                System.Threading.Interlocked.Add(ref this.framesToParseQueuedByteCount, frame.Data.Length);

            }
        }
        internal void ParseFramesInFrameQueue() {
            Frame frame = null;
            try {
                while (true) {

                    frame = this.framesToParseQueue.Take();
                    System.Threading.Interlocked.Add(ref this.framesToParseQueuedByteCount, -frame.Data.Length);
                    this.UpdateBufferUsagePercent();
                    this.ParseFrame(frame);
                }
            }
            catch (System.Threading.ThreadAbortException) {
                throw;
            }
#if !DEBUG
            catch (Exception e) {
                if(frame != null)
                    SharedUtils.Logger.Log("Error parsing " + frame.ToString() + ". " + e.ToString(), SharedUtils.Logger.EventLogEntryType.Warning);
                if (this.UnhandledException == null)
                    throw;
                else
                    this.UnhandledException(AppDomain.CurrentDomain, new UnhandledExceptionEventArgs(e, true));
            }
#endif
        }


        public Frame GetFrame(DateTime timestamp, byte[] data, SharedUtils.Pcap.PcapFrame.DataLinkTypeEnum dataLinkType, object tag = null) {

            Type packetType = Packets.PacketFactory.GetPacketType(dataLinkType);
            return new Frame(timestamp, data, packetType, ++nFramesReceived) { Tag = tag };
        }

        internal Frame GetFrame(SharedUtils.Pcap.PacketReceivedEventArgs packet) {
            //Frame receivedFrame=null;

            if (packet.PacketType == SharedUtils.Pcap.PacketReceivedEventArgs.PacketTypes.Ethernet2Packet) {
                return new Frame(packet.Timestamp, packet.Data, typeof(Packets.Ethernet2Packet), ++nFramesReceived);
            }
            else if (packet.PacketType == SharedUtils.Pcap.PacketReceivedEventArgs.PacketTypes.IPv4Packet) {
                return new Frame(packet.Timestamp, packet.Data, typeof(Packets.IPv4Packet), ++nFramesReceived);
            }
            else if (packet.PacketType == SharedUtils.Pcap.PacketReceivedEventArgs.PacketTypes.IPv6Packet) {
                return new Frame(packet.Timestamp, packet.Data, typeof(Packets.IPv6Packet), ++nFramesReceived);
            }
            else if (packet.PacketType == SharedUtils.Pcap.PacketReceivedEventArgs.PacketTypes.IEEE_802_11Packet) {
                return new Frame(packet.Timestamp, packet.Data, typeof(Packets.IEEE_802_11Packet), ++nFramesReceived);
            }
            else if (packet.PacketType == SharedUtils.Pcap.PacketReceivedEventArgs.PacketTypes.IEEE_802_11RadiotapPacket) {
                return new Frame(packet.Timestamp, packet.Data, typeof(Packets.IEEE_802_11RadiotapPacket), ++nFramesReceived);
            }
            else if (packet.PacketType == SharedUtils.Pcap.PacketReceivedEventArgs.PacketTypes.CiscoHDLC) {
                return new Frame(packet.Timestamp, packet.Data, typeof(Packets.CiscoHdlcPacket), ++nFramesReceived);
            }
            else if (packet.PacketType == SharedUtils.Pcap.PacketReceivedEventArgs.PacketTypes.LinuxCookedCapture) {
                return new Frame(packet.Timestamp, packet.Data, typeof(Packets.LinuxCookedCapture), ++nFramesReceived);
            }
            else if (packet.PacketType == SharedUtils.Pcap.PacketReceivedEventArgs.PacketTypes.LinuxCookedCapture2) {
                return new Frame(packet.Timestamp, packet.Data, typeof(Packets.LinuxCookedCapture2), ++nFramesReceived);
            }
            else if (packet.PacketType == SharedUtils.Pcap.PacketReceivedEventArgs.PacketTypes.PrismCaptureHeader) {
                return new Frame(packet.Timestamp, packet.Data, typeof(Packets.PrismCaptureHeaderPacket), ++nFramesReceived);
            }
            else if (packet.PacketType == SharedUtils.Pcap.PacketReceivedEventArgs.PacketTypes.NullLoopback) {
                return new Frame(packet.Timestamp, packet.Data, typeof(Packets.NullLoopbackPacket), ++nFramesReceived);
            }

            else
                return null;
        }

        private bool FilterMatches(Packets.IPv4Packet ipv4Packet, Packets.IPv6Packet ipv6Packet, Packets.TcpPacket tcpPacket, Packets.UdpPacket udpPacket) {
            if (this.InputFilter == null)
                return true;

            string transportProtocol;
            ushort srcPort, dstPort;
            if (tcpPacket != null) {
                transportProtocol = "TCP";
                srcPort = tcpPacket.SourcePort;
                dstPort = tcpPacket.DestinationPort;
            }
            else if (udpPacket != null) {
                transportProtocol = "UDP";
                srcPort = udpPacket.SourcePort;
                dstPort = udpPacket.DestinationPort;
            }
            else
                return false;

            if (ipv4Packet != null) {
                return this.InputFilter.Matches(new IPEndPoint(ipv4Packet.SourceIPAddress, srcPort), new IPEndPoint(ipv4Packet.DestinationIPAddress, dstPort), transportProtocol);
            }
            else if (ipv6Packet != null) {
                return this.InputFilter.Matches(new IPEndPoint(ipv6Packet.SourceIPAddress, srcPort), new IPEndPoint(ipv6Packet.DestinationIPAddress, dstPort), transportProtocol);
            }
            else
                return false;
        }

        internal void ParseFrame(Frame receivedFrame) {

            if (receivedFrame != null) {


                this.nBytesReceived += receivedFrame.Data.Length;

                Packets.Ethernet2Packet ethernet2Packet = null;
                Packets.IEEE_802_11Packet WlanPacket = null;
                Packets.ArpPacket arpPacket = null;
                Packets.IPv4Packet ipv4Packet = null;
                Packets.IPv6Packet ipv6Packet = null;
                Packets.TcpPacket tcpPacket = null;
                Packets.UdpPacket udpPacket = null;
                Packets.RawPacket rawPacket = null;
                List<ushort> vlanIdList = new List<ushort>();
                HashSet<Type> packetTypeSet = new HashSet<Type>();

                foreach (Packets.AbstractPacket p in receivedFrame.PacketList) {
                    //p.GetType() call takes ~4ns according to Jon Skeet: https://stackoverflow.com/questions/353342/performance-of-object-gettype/353435#353435
                    Type type = p.GetType();
                    if (!packetTypeSet.Contains(type))
                        packetTypeSet.Add(type);

                    if (type == typeof(Packets.IPv4Packet))
                        ipv4Packet = (Packets.IPv4Packet)p;
                    else if (type == typeof(Packets.IPv6Packet))
                        ipv6Packet = (Packets.IPv6Packet)p;
                    else if (type == typeof(Packets.TcpPacket))
                        tcpPacket = (Packets.TcpPacket)p;
                    else if (type == typeof(Packets.UdpPacket))
                        udpPacket = (Packets.UdpPacket)p;
                    else if (type == typeof(Packets.Ethernet2Packet))
                        ethernet2Packet = (Packets.Ethernet2Packet)p;
                    else if (type == typeof(Packets.IEEE_802_11Packet))
                        WlanPacket = (Packets.IEEE_802_11Packet)p;
                    else if (type == typeof(Packets.ArpPacket))
                        arpPacket = (Packets.ArpPacket)p;
                    else if (type == typeof(Packets.RawPacket))
                        rawPacket = (Packets.RawPacket)p;
                    else if (type == typeof(Packets.IEEE_802_1Q_VlanPacket)) {
                        ushort vlanId = ((Packets.IEEE_802_1Q_VlanPacket)p).VlanID;
                        if (!vlanIdList.Contains(vlanId))
                            vlanIdList.Add(vlanId);
                    }
                    else if (type == typeof(Packets.Erspan)) {
                        ushort? vlanId = ((Packets.Erspan)p).VlanID;
                        if (vlanId.HasValue && !vlanIdList.Contains(vlanId.Value))
                            vlanIdList.Add(vlanId.Value);
                    }
                }

                if (this.FilterMatches(ipv4Packet, ipv6Packet, tcpPacket, udpPacket)) {

                    foreach (var error in receivedFrame.Errors) {
                        this.OnAnomalyDetected(error.ToString(), receivedFrame.Timestamp);
                    }
                    this.OnFrameDetected(new Events.FrameEventArgs(receivedFrame));
                    if (ethernet2Packet != null && arpPacket != null) {
                        //this must be done also for IEEE 802.11 !!!
                        this.ExtractArpData(ethernet2Packet, arpPacket);
                    }


                    NetworkPacket networkPacket = null;
                    if (ipv4Packet == null && ipv6Packet == null) {
                        foreach (PacketHandlers.IPacketHandler packetHandler in nonIpPacketHandlerList) {
                            if (packetHandler.CanParse(packetTypeSet)) {
                                try {
                                    NetworkHost sourceHost = new NetworkHost(IPAddress.None);
                                    packetHandler.ExtractData(ref sourceHost, null, receivedFrame.PacketList/*.Values*/);
                                }
                                catch (Exception ex) {
                                    this.OnAnomalyDetected("Error applying " + packetHandler.ToString() + " packet handler to frame " + receivedFrame.ToString() + ": " + ex.Message, receivedFrame.Timestamp);
                                }
                            }
                        }

                    }
                    else if (ipv4Packet != null || ipv6Packet != null) {
                        byte ipTTL;
                        IPAddress ipPacketSourceIp;
                        IPAddress ipPacketDestinationIp;
                        Packets.AbstractPacket ipPacket;
                        if (ipv6Packet != null) {
                            ipTTL = ipv6Packet.HopLimit;
                            ipPacketSourceIp = ipv6Packet.SourceIPAddress;
                            ipPacketDestinationIp = ipv6Packet.DestinationIPAddress;
                            ipPacket = ipv6Packet;
                        }
                        else {//if(ipv4Packet!=null) {
                            ipTTL = ipv4Packet.TimeToLive;
                            ipPacketSourceIp = ipv4Packet.SourceIPAddress;
                            ipPacketDestinationIp = ipv4Packet.DestinationIPAddress;
                            ipPacket = ipv4Packet;
                        }

                        NetworkHost sourceHost, destinationHost;
                        //source
                        if (NetworkHostList.ContainsIP(ipPacketSourceIp))
                            sourceHost = NetworkHostList.GetNetworkHost(ipPacketSourceIp);
                        else {
                            sourceHost = new NetworkHost(ipPacketSourceIp);
                            lock (this.NetworkHostList)
                                this.NetworkHostList.Add(sourceHost);
                            this.OnNetworkHostDetected(new Events.NetworkHostEventArgs(sourceHost));
                        }
                        if (NetworkHostList.ContainsIP(ipPacketDestinationIp))
                            destinationHost = NetworkHostList.GetNetworkHost(ipPacketDestinationIp);
                        else {
                            destinationHost = new NetworkHost(ipPacketDestinationIp);
                            lock (this.NetworkHostList)
                                this.NetworkHostList.Add(destinationHost);
                            this.OnNetworkHostDetected(new Events.NetworkHostEventArgs(destinationHost));
                        }
                        //we now have sourceHost and destinationHost
                        networkPacket = new NetworkPacket(sourceHost, destinationHost, ipPacket);

                        if (ethernet2Packet != null) {
                            if (sourceHost.MacAddress != ethernet2Packet.SourceMACAddress) {
                                if (sourceHost.MacAddress != null && ethernet2Packet.SourceMACAddress != null && sourceHost.MacAddress.ToString() != ethernet2Packet.SourceMACAddress.ToString())
                                    if (!sourceHost.IsRecentMacAddress(ethernet2Packet.SourceMACAddress) && !sourceHost.IPAddress.Equals(IPAddress.Parse("0.0.0.0")))
                                        this.OnAnomalyDetected("Ethernet MAC has changed, possible ARP spoofing! IP " + sourceHost.IPAddress.ToString() + ", MAC " + sourceHost.MacAddress.ToString() + " -> " + ethernet2Packet.SourceMACAddress.ToString() + " (frame " + receivedFrame.FrameNumber + ")", receivedFrame.Timestamp);
                                sourceHost.MacAddress = ethernet2Packet.SourceMACAddress;
                            }
                            if (destinationHost.MacAddress != ethernet2Packet.DestinationMACAddress) {
                                if (destinationHost.MacAddress != null && ethernet2Packet.DestinationMACAddress != null && destinationHost.MacAddress.ToString() != ethernet2Packet.DestinationMACAddress.ToString())
                                    if (!destinationHost.IsRecentMacAddress(ethernet2Packet.DestinationMACAddress) && !destinationHost.IPAddress.Equals(IPAddress.Parse("0.0.0.0")))
                                        this.OnAnomalyDetected("Ethernet MAC has changed, possible ARP spoofing! IP " + destinationHost.IPAddress.ToString() + ", MAC " + destinationHost.MacAddress.ToString() + " -> " + ethernet2Packet.DestinationMACAddress.ToString() + " (frame " + receivedFrame.FrameNumber + ")", receivedFrame.Timestamp);
                                destinationHost.MacAddress = ethernet2Packet.DestinationMACAddress;
                            }
                        }
                        else if (WlanPacket != null) {
                            sourceHost.MacAddress = WlanPacket.SourceMAC;
                            destinationHost.MacAddress = WlanPacket.DestinationMAC;
                        }

                        /*OS Fingerprinting code used to be here, but has been moved further down in order to be after the PacketHandlers*/
                        //this one checks for OS's in all sorts of packets (for example TCP SYN or DHCP Request)

                        FiveTuple fiveTuple = null;

                        if (tcpPacket != null) {
                            networkPacket.SetTcpData(tcpPacket);

                            NetworkTcpSession networkTcpSession = GetNetworkTcpSession(tcpPacket, sourceHost, destinationHost);

                            if (networkTcpSession != null) {
                                //add packet to session
                                if (networkTcpSession.TryAddPacket(tcpPacket, sourceHost, destinationHost))
                                    fiveTuple = networkTcpSession.Flow.FiveTuple;
                                else
                                    networkTcpSession = null;//the packet did apparently not belong to the TCP session
                            }





                            if (networkTcpSession != null) {
                                ExtractTcpSessionData(sourceHost, destinationHost, networkTcpSession, receivedFrame, tcpPacket);
                            }

                        }//end TCP packet
                        else if (udpPacket != null) {
                            networkPacket.SetUdpData(udpPacket);
                            fiveTuple = new FiveTuple(sourceHost, udpPacket.SourcePort, destinationHost, udpPacket.DestinationPort, FiveTuple.TransportProtocol.UDP);
                        }

                        foreach (ushort vlanID in vlanIdList) {
                            sourceHost.AddVlanID(vlanID);
                            destinationHost.AddVlanID(vlanID);
                        }
                        sourceHost.AddTtl(ipTTL);

                        //this one is just extra for hosts which don't use TCP for example and therefore can't use the OS fingerprinter to get the TTL distance
                        if (sourceHost.TtlDistance == byte.MaxValue) {//maxValue=default if no TtlDistance exists
                            //RFC 4890:
                            //Multicast Router Discovery messages (must have link-local source address and hop limit = 1)
                            if (ipPacketSourceIp.IsIPv6LinkLocal && ipTTL == 1)
                                sourceHost.AddProbableTtlDistance(0);
                            else {
                                foreach (Fingerprints.IOsFingerprinter fingerprinter in this.OsFingerprintCollectionList)
                                    if (typeof(Fingerprints.ITtlDistanceCalculator).IsAssignableFrom(fingerprinter.GetType()))
                                        sourceHost.AddProbableTtlDistance(((Fingerprints.ITtlDistanceCalculator)fingerprinter).GetTtlDistance(ipTTL));
                            }
                        }
                        if (receivedFrame.Tag != null && receivedFrame.Tag is IEnumerable<KeyValuePair<string, string>> packetTags && sourceHost.TtlDistance == 0) {
                            foreach (KeyValuePair<string, string> tag in packetTags) {
                                if (tag.Key.Equals("Longitude", StringComparison.InvariantCultureIgnoreCase) || tag.Key.Equals("Latitude", StringComparison.InvariantCultureIgnoreCase))
                                    sourceHost.AddNumberedExtraDetail(tag.Key, tag.Value);
                            }
                        }


                        //Iterate through all PacketHandlers for packets not inside a TCP stream
                        //It can also be the case that the packet can be either inside TCP or inside UDP (such as the NetBiosNameServicePacket)
                        //All packets here must however always be complete in each frame (i.e. no TCP reassembly is being done)
                        foreach (PacketHandlers.IPacketHandler packetHandler in packetHandlerList) {
                            if (packetHandler.CanParse(packetTypeSet)) {
                                try {
                                    packetHandler.ExtractData(ref sourceHost, destinationHost, receivedFrame.PacketList);
                                }
                                catch (Exception ex) {
                                    this.OnAnomalyDetected("Error applying " + packetHandler.ToString() + " packet handler to frame " + receivedFrame.ToString() + ": " + ex.Message, receivedFrame.Timestamp);
                                    if (ex is IOException)//we might have created too many temp files
                                        FileTransfer.FileStreamAssemblerList.RemoveTempFiles();
                                }
                            }
                        }

                        foreach (Fingerprints.IOsFingerprinter fingerprinter in this.OsFingerprintCollectionList) {

                            if (fingerprinter.TryGetOperatingSystems(out IList<Fingerprints.DeviceFingerprint> osList, receivedFrame.PacketList)) {
                                if (osList != null && osList.Count > 0) {
                                    foreach (PacketParser.Fingerprints.DeviceFingerprint os in osList) {
                                        sourceHost.AddProbableOs(os.ToString(), fingerprinter, 1.0 / osList.Count);
                                        if (os.Category != null && os.Category.Length > 0)
                                            sourceHost.AddProbableDeviceCategory(os.Category, fingerprinter, 1.0 / osList.Count);
                                        if (os.Family != null && os.Family.Length > 0)
                                            sourceHost.AddProbableDeviceFamily(os.Family, fingerprinter, 1.0 / osList.Count);
                                    }
                                    if (typeof(Fingerprints.ITtlDistanceCalculator).IsAssignableFrom(fingerprinter.GetType())) {
                                        byte ttlDistance;
                                        if (((Fingerprints.ITtlDistanceCalculator)fingerprinter).TryGetTtlDistance(out ttlDistance, receivedFrame.PacketList))
                                            sourceHost.AddProbableTtlDistance(ttlDistance);
                                    }
                                }
                            }
                        }


                    }//end of IP packet if clause


                    if (networkPacket != null) {
                        networkPacket.SourceHost.SentPackets.Add(networkPacket);
                        networkPacket.DestinationHost.ReceivedPackets.Add(networkPacket);
                    }

                    //check the frame content for cleartext
                    this.CheckFrameCleartext(receivedFrame);

                    //check the frame content for keywords
                    foreach (byte[] keyword in this.keywordList) {
                        //jAG SLUTADE H�R. FUNKAR EJ VID RELOAD
                        int keyIndex = receivedFrame.IndexOf(keyword);
                        if (keyIndex >= 0) {
                            if (networkPacket != null)
                                if (networkPacket.SourceTcpPort != null && networkPacket.DestinationTcpPort != null)
                                    this.OnKeywordDetected(new Events.KeywordEventArgs(receivedFrame, keyIndex, keyword.Length, networkPacket.SourceHost, networkPacket.DestinationHost, "TCP " + networkPacket.SourceTcpPort.ToString(), "TCP " + networkPacket.DestinationTcpPort.ToString()));
                                //this.parentForm.AddDetectedKeyword(receivedFrame, keyIndex, keyword.Length, networkPacket.SourceHost, networkPacket.DestinationHost, "TCP "+networkPacket.SourceTcpPort.ToString(), "TCP "+networkPacket.DestinationTcpPort.ToString());
                                else if (networkPacket.SourceUdpPort != null && networkPacket.DestinationUdpPort != null)
                                    this.OnKeywordDetected(new Events.KeywordEventArgs(receivedFrame, keyIndex, keyword.Length, networkPacket.SourceHost, networkPacket.DestinationHost, "UDP " + networkPacket.SourceUdpPort.ToString(), "UDP " + networkPacket.DestinationUdpPort.ToString()));
                                else
                                    this.OnKeywordDetected(new Events.KeywordEventArgs(receivedFrame, keyIndex, keyword.Length, networkPacket.SourceHost, networkPacket.DestinationHost, "", ""));
                            else
                                this.OnKeywordDetected(new Events.KeywordEventArgs(receivedFrame, keyIndex, keyword.Length, null, null, "", ""));


                        }
                    }
                }//end of filter if clause

            }//end of receivedFrame

        }



        private void ExtractTcpSessionData(NetworkHost sourceHost, NetworkHost destinationHost, NetworkTcpSession networkTcpSession, Frame receivedFrame, Packets.TcpPacket tcpPacket) {
            NetworkTcpSession.TcpDataStream currentStream = null;
            bool transferIsClientToServer;
            if (networkTcpSession.ClientHost == sourceHost && networkTcpSession.ClientTcpPort == tcpPacket.SourcePort) {
                currentStream = networkTcpSession.ClientToServerTcpDataStream;
                transferIsClientToServer = true;
            }
            else if (networkTcpSession.ServerHost == sourceHost && networkTcpSession.ServerTcpPort == tcpPacket.SourcePort) {
                currentStream = networkTcpSession.ServerToClientTcpDataStream;
                transferIsClientToServer = false;
            }
            else
                throw new Exception("Wrong TCP Session received");


            if (currentStream != null && tcpPacket.PayloadDataLength > 0) {
                //0: Check the number of sequenced packets!
                NetworkTcpSession.TcpDataStream.VirtualTcpData virtualTcpData = currentStream.GetNextVirtualTcpData();

                bool nextVirtualFrameIsTrailingDataInTcpSegment = false;
                while (virtualTcpData != null && currentStream.CountBytesToRead() > 0) {
                    //1: check if there is an active file stream assembly going on...
                    //   if yes: add the virtualTcpData to the stream
                    if(this.TcpStreamAssemblerList.ContainsKey((networkTcpSession, transferIsClientToServer))) {
                        var flowKey = (networkTcpSession, transferIsClientToServer);
                        var tcpStreamAssembler = this.TcpStreamAssemblerList[flowKey];
                        int bytesMoved = tcpStreamAssembler.AddData(virtualTcpData.GetBytes(false), virtualTcpData.FirstPacketSequenceNumber);
                        currentStream.RemoveData(bytesMoved);
                        if(tcpStreamAssembler.IsCompleted) {
                            tcpStreamAssembler.Finish();
                            this.TcpStreamAssemblerList.Remove(flowKey);
                        }
                    }
                    else if (this.FileStreamAssemblerList.ContainsAssembler(networkTcpSession.Flow.FiveTuple, transferIsClientToServer, true)) {
                        //this could be any type of TCP packet... but probably part of a file transfer...
                        FileTransfer.FileStreamAssembler assembler = this.FileStreamAssemblerList.GetAssembler(networkTcpSession.Flow.FiveTuple, transferIsClientToServer);
                        //HTTP 1.0 (but sometimes 1.1) sends a FIN flag when the last packet of a file is sent
                        //See: http://www.mail-archive.com/wireshark-dev@wireshark.org/msg08695.html
                        //This is also useful for FTP data transfers
                        if (assembler.FileContentLength == -1 &&
                            assembler.FileSegmentRemainingBytes == -1 &&
                            tcpPacket.FlagBits.Fin) {//the last packet of the file

                            assembler.SetRemainingBytesInFile(virtualTcpData.GetBytes(false).Length);
                            assembler.FileSegmentRemainingBytes = virtualTcpData.GetBytes(false).Length;
                        }
                        if (assembler.FileStreamType == FileTransfer.FileStreamTypes.HttpGetChunked ||
                            assembler.FileStreamType == FileTransfer.FileStreamTypes.HttpPostMimeMultipartFormData ||
                            assembler.FileStreamType == FileTransfer.FileStreamTypes.OscarFileTransfer ||
                            assembler.FileSegmentRemainingBytes >= virtualTcpData.ByteCount ||
                            (assembler.FileContentLength == -1 && assembler.FileSegmentRemainingBytes == -1)) {

                            assembler.AddData(virtualTcpData.GetBytes(false), virtualTcpData.FirstPacketSequenceNumber);
                            currentStream.RemoveData(virtualTcpData);
                        }
                        else if (assembler.FileSegmentRemainingBytes > 0 &&
                            assembler.FileSegmentRemainingBytes < virtualTcpData.ByteCount &&
                            !assembler.IsSegmentLengthDataTransferType) {
                            //allow all file transfer types to be completed with a subset of a TCP segment
                            //This code sement was previously only used for these file stream types:
                            //HttpGetNormal, LPD, Meterpreter, VNC, njRAT(?)

                            byte[] allBytes = virtualTcpData.GetBytes(false);
                            byte[] trimmedBytes = new byte[assembler.FileSegmentRemainingBytes];
                            Array.Copy(allBytes, trimmedBytes, trimmedBytes.Length);
                            assembler.AddData(trimmedBytes, virtualTcpData.FirstPacketSequenceNumber);
                            currentStream.RemoveData(trimmedBytes.Length);
                        }
                        else {
                            //regardless if I could use the data or not I will now remove it from the stream since the data is already in the assembler now
                            currentStream.RemoveData(virtualTcpData);
                        }
                    }

                    //2: if no file stream: try to reassemble a sub-TCP packet
                    //   if yes: parse it
                    else if (networkTcpSession.RequiredNextTcpDataStream == null || networkTcpSession.RequiredNextTcpDataStream == currentStream) {
                        if (networkTcpSession.RequiredNextTcpDataStream != null)
                            networkTcpSession.RequiredNextTcpDataStream = null; //reset next packet source, only used at the start of stream
                        byte[] virtualTcpBytes = virtualTcpData.GetBytes(true);
                        Frame virtualFrame = new Frame(receivedFrame.Timestamp, virtualTcpBytes, typeof(Packets.TcpPacket), receivedFrame.FrameNumber, false, false, virtualTcpBytes.Length);

                        List<Packets.AbstractPacket> packetList = new List<PacketParser.Packets.AbstractPacket>();
                        HashSet<Type> packetTypeSet = new HashSet<Type>();
                        try {
                            if (virtualFrame.BasePacket != null) {
                                Packets.TcpPacket virtualTcpPacket = (Packets.TcpPacket)virtualFrame.BasePacket;
                                virtualTcpPacket.IsVirtualPacketFromTrailingDataInTcpSegment = nextVirtualFrameIsTrailingDataInTcpSegment;
                                packetList.AddRange(virtualTcpPacket.GetSubPackets(true, networkTcpSession.ProtocolFinder, currentStream == networkTcpSession.ClientToServerTcpDataStream));
                            }
                            foreach (var parsingError in virtualFrame.Errors) {
                                this.OnAnomalyDetected("Frame " + virtualFrame.FrameNumber + " : " + parsingError.ToString(), virtualFrame.Timestamp);
                            }
                            int parsedBytes = 0;
                            foreach (var p in packetList) {
                                packetTypeSet.Add(p.GetType());
                            }
                            foreach (PacketHandlers.ITcpSessionPacketHandler packetHandler in this.tcpSessionPacketHandlerList) {
                                if (packetHandler.CanParse(packetTypeSet)) {
                                    parsedBytes += packetHandler.ExtractData(networkTcpSession, transferIsClientToServer, packetList);
                                }
                            }
                            if (parsedBytes > 0) {
                                nextVirtualFrameIsTrailingDataInTcpSegment = false;
                                if (parsedBytes >= virtualTcpData.ByteCount)
                                    networkTcpSession.RemoveData(virtualTcpData, sourceHost, tcpPacket.SourcePort);
                                else {
                                    networkTcpSession.RemoveData(virtualTcpData.FirstPacketSequenceNumber, parsedBytes, sourceHost, tcpPacket.SourcePort);

                                    if (currentStream.CountBytesToRead() > 0 && currentStream.CountBytesToRead() < tcpPacket.PayloadDataLength) {
                                        nextVirtualFrameIsTrailingDataInTcpSegment = true;
                                    }
                                }
                            }
                        }
                        catch (Exception e) {
                            this.OnAnomalyDetected(new Events.AnomalyEventArgs("Error parsing TCP contents of frame " + tcpPacket.ParentFrame.FrameNumber + " (src " + tcpPacket.SourcePort + ", dst " + tcpPacket.DestinationPort + ") : " + e.ToString(), tcpPacket.ParentFrame.Timestamp));
                        }

                    }

                    //   if no: try to read more packets to the virtyalTcpData
                    virtualTcpData = currentStream.GetNextVirtualTcpData();
                }



            }
            else {
                //we now have a tcpPacket without a payload. Probably a SYN, ACK or other control packet

                //some packets (such as FTP) need to see new upcoming TCP sessions since they identify/activate file transfer sessions that way

                HashSet<Type> packetTypeSet = new HashSet<Type>();
                foreach (var p in receivedFrame.PacketList)
                    packetTypeSet.Add(p.GetType());

                foreach (PacketHandlers.ITcpSessionPacketHandler packetHandler in this.tcpSessionPacketHandlerList) {
                    if (packetHandler.CanParse(packetTypeSet)) {

                        packetHandler.ExtractData(networkTcpSession, transferIsClientToServer, receivedFrame.PacketList/*.Values*/);
                        //skip the return value, it isn't needed here
                    }
                }
            }
            if (networkTcpSession.FinPacketReceived || networkTcpSession.SessionClosed) {

                //see if there is a file stream assembler and close it
                this.closeAssemblerIfExists(networkTcpSession.Flow.FiveTuple, true);
                this.closeAssemblerIfExists(networkTcpSession.Flow.FiveTuple, false);



            }

        }

        private void closeAssemblerIfExists(NetworkHost sourceHost, ushort sourcePort, NetworkHost destinationHost, ushort destinationPort, FiveTuple.TransportProtocol transport, bool transferIsClientToServer) {
            FiveTuple tmpFiveTuple = new FiveTuple(sourceHost, sourcePort, destinationHost, destinationPort, transport);
            this.closeAssemblerIfExists(tmpFiveTuple, transferIsClientToServer);
        }

        private void closeAssemblerIfExists(FiveTuple fiveTuple, bool transferIsClientToServer) {
            if (this.FileStreamAssemblerList.ContainsAssembler(fiveTuple, transferIsClientToServer, true)) {
                //we have an assembler, let's close it
                using (FileTransfer.FileStreamAssembler assembler = this.FileStreamAssemblerList.GetAssembler(fiveTuple, transferIsClientToServer)) {
                    if (assembler.IsActive && assembler.AssembledByteCount > 0 && (assembler.FileSegmentRemainingBytes <= 0 || assembler.ContentRange != null)) {
                        //I'll assume that the file transfer was OK
                        assembler.FinishAssembling();
                    }
                    else {
                        this.FileStreamAssemblerList.Remove(assembler, true);
                    }
                }
            }
        }



        private void AddNetworkTcpSessionToPool(NetworkTcpSession session) {
            int sessionHash = session.GetHashCode();
            if (this.networkTcpSessionList.ContainsKey(sessionHash))//earlier session with the same IP's and port numbers
                this.networkTcpSessionList[sessionHash] = session;//replace the old with the new in the dictionary only!
            else
                this.networkTcpSessionList.Add(sessionHash, session);
        }
        private NetworkTcpSession GetNetworkTcpSession(Packets.TcpPacket tcpPacket, NetworkHost sourceHost, NetworkHost destinationHost) {
            if (tcpPacket.FlagBits.Synchronize) {
                if (!tcpPacket.FlagBits.Acknowledgement) {//the first SYN packet
                    NetworkTcpSession session = new NetworkTcpSession(tcpPacket, sourceHost, destinationHost, this.ProtocolFinderFactory, this.toCustomTimeZoneStringFunction);
                    this.AddNetworkTcpSessionToPool(session);
                    return session;
                }
                else {//SYN+ACK packet (server -> client)
                    int key = NetworkTcpSession.GetHashCode(destinationHost, sourceHost, tcpPacket.DestinationPort, tcpPacket.SourcePort);

                    if (this.networkTcpSessionList.ContainsKey(key)) {
                        NetworkTcpSession session = networkTcpSessionList[key];
                        if (session.SynPacketReceived && !session.SynAckPacketReceived) {//we now have an established session

                            return session;
                        }
                        else
                            return null;
                    }
                    else//stray SYN+ACK packet
                        return null;

                }
            }
            else {//No SYN packet. There should be an active session

                int clientToServerKey = NetworkTcpSession.GetHashCode(sourceHost, destinationHost, tcpPacket.SourcePort, tcpPacket.DestinationPort);
                int serverToClientKey = NetworkTcpSession.GetHashCode(destinationHost, sourceHost, tcpPacket.DestinationPort, tcpPacket.SourcePort);


                if (this.networkTcpSessionList.ContainsKey(clientToServerKey)) {//see if packet is client to server
                    NetworkTcpSession session = this.networkTcpSessionList[clientToServerKey];
                    if (session.SynAckPacketReceived)
                        return session;
                    else
                        return null;
                }
                else if (this.networkTcpSessionList.ContainsKey(serverToClientKey)) {//see if packet is server to client
                    NetworkTcpSession session = this.networkTcpSessionList[serverToClientKey];
                    if (session.SynAckPacketReceived)
                        return session;
                    else {
                        return null;
                    }
                }
                else {//no such session.... exists. Try to create a new non-complete session
                    NetworkTcpSession session = new NetworkTcpSession(sourceHost, destinationHost, tcpPacket, this.ProtocolFinderFactory, this.toCustomTimeZoneStringFunction);//create a truncated session
                    this.AddNetworkTcpSessionToPool(session);
                    return session;
                }
            }
        }


        internal void AddReconstructedFile(FileTransfer.ReconstructedFile file) {
            //let's timestomp the last write time of the file before passing it on
            try {
                System.IO.File.SetLastWriteTime(file.FilePath, file.Timestamp);
            }
            catch (Exception e) {
                this.OnAnomalyDetected("Error timestomping reconstructed file: " + e.Message);
            }

            this.ReconstructedFileList.Add(file);
            this.OnFileReconstructed(new Events.FileEventArgs(file, this.useRelativePathIfAvailable));
        }
        internal void AddCredential(NetworkCredential credential) {

            if (!credentialList.ContainsKey(credential.Key)) {
                this.credentialList.Add(credential.Key, credential);
                if (credential.Password != null)
                    this.OnCredentialDetected(new Events.CredentialEventArgs(credential));
                //parentForm.ShowCredential(credential);
            }

        }
        public IList<NetworkCredential> GetCredentials() {
            return this.credentialList.Values;
        }


        private void ExtractArpData(Packets.IEEE_802_11Packet wlanPacket, Packets.ArpPacket arpPacket) {
            if (arpPacket.SenderIPAddress != null && wlanPacket != null)
                ExtractArpData(wlanPacket.SourceMAC, arpPacket);
        }
        private void ExtractArpData(Packets.Ethernet2Packet ethernet2Packet, Packets.ArpPacket arpPacket) {
            if (arpPacket.SenderIPAddress != null && ethernet2Packet != null)
                ExtractArpData(ethernet2Packet.SourceMACAddress, arpPacket);
        }
        private void ExtractArpData(System.Net.NetworkInformation.PhysicalAddress sourceMAC, Packets.ArpPacket arpPacket) {
            if (sourceMAC != null) {
                if (arpPacket.SenderHardwareAddress.Equals(sourceMAC)) {
                    NetworkHost host = null;
                    if (!this.NetworkHostList.ContainsIP(arpPacket.SenderIPAddress)) {
                        host = new NetworkHost(arpPacket.SenderIPAddress);
                        host.MacAddress = arpPacket.SenderHardwareAddress;
                        lock (this.NetworkHostList)
                            this.NetworkHostList.Add(host);
                        //parentForm.ShowDetectedHost(host);
                        this.OnNetworkHostDetected(new Events.NetworkHostEventArgs(host));
                    }
                    if (host != null)
                        host.AddQueriedIP(arpPacket.TargetIPAddress);

                }
                else {
                    this.OnAnomalyDetected(
                        "Different source MAC addresses in Ethernet and ARP packet: " +
                                "Ethernet MAC=" + sourceMAC +
                                ", ARP MAC=" + arpPacket.SenderHardwareAddress +
                                ", ARP IP=" + arpPacket.SenderIPAddress +
                                " (frame: " + arpPacket.ParentFrame.ToString() + ")", arpPacket.ParentFrame.Timestamp);
                }

            }
        }
               
        internal void ExtractMultipartFormData(IEnumerable<Mime.MultipartPart> formMultipartData, FiveTuple fiveTuple, bool transferIsClientToServer, DateTime timestamp, long frameNumber, ApplicationLayerProtocol applicationLayerProtocol, System.Collections.Specialized.NameValueCollection cookieParams = null, string domain = null) {
            System.Collections.Specialized.NameValueCollection formParameters = new System.Collections.Specialized.NameValueCollection();

            foreach (Mime.MultipartPart part in formMultipartData) {
                if (part.Attributes != null && part.Attributes.Count > 0) {
                    if (part.Data != null && part.Data.Length > 0) {
                        //lookup name and convert multipart data to string
                        string attributeName = part.Attributes["name"];
                        foreach (string key in part.Attributes) {
                            if (key == "name")
                                attributeName = part.Attributes["name"];
                            else
                                formParameters.Add(key, part.Attributes[key]);
                        }

                        int partDataTruncateSize = 250;//max 250 characters

                        string partData = Utils.ByteConverter.ReadString(part.Data, 0, partDataTruncateSize).Trim();

                        if (attributeName != null && attributeName.Length > 0) {
                            if (partData != null && partData.Length > 0) {
                                formParameters.Add(attributeName, partData);
                                if (part.Data.Length > partDataTruncateSize) {
                                    //create a fake "filename" header to save full data to disk if it has been truncated
                                    if (formParameters["filename"] == null) {
                                        string filename = attributeName;
                                        if (formParameters["Content-Type"]?.Length > 0)
                                            filename = PacketHandlers.HttpPacketHandler.AppendMimeContentTypeAsExtension(filename, formParameters["Content-Type"]);
                                        part.Attributes.Add("filename", filename);
                                    }
                                }
                            }
                            else
                                formParameters.Add("name", attributeName);
                        }
                    }
                    else {
                        formParameters.Add(part.Attributes);
                    }
                }
            }
            if (formParameters.Count > 0)
                this.OnParametersDetected(new Events.ParametersEventArgs(frameNumber, fiveTuple, transferIsClientToServer, formParameters, timestamp, "MIME/MultiPart"));
            //check for credentials (usernames and passwords)
            NetworkCredential credential = NetworkCredential.GetNetworkCredential(formParameters, fiveTuple.ClientHost, fiveTuple.ServerHost, "MIME/MultiPart", timestamp, domain);

            if (credential != null && credential.Username != null && credential.Password != null) {
                //mainPacketHandler.AddCredential(new NetworkCredential(sourceHost, destinationHost, "HTTP POST", username, password, httpPacket.ParentFrame.Timestamp));
                this.AddCredential(credential);
            }

            //add cookies if they exist
            if (cookieParams != null)
                foreach (string key in cookieParams.Keys)
                    if (formParameters[key] == null)
                        formParameters[key] = cookieParams[key];

            PacketParser.Events.MessageEventArgs messageEventArgs = GetMessageEventArgs(applicationLayerProtocol, fiveTuple, transferIsClientToServer, frameNumber, timestamp, formParameters);
            if (messageEventArgs != null)
                this.OnMessageDetected(messageEventArgs);
        }

        private PacketParser.Events.MessageEventArgs GetMessageEventArgs(ApplicationLayerProtocol applicationLayerProtocol, FiveTuple fiveTuple, bool transferIsClientToServer, long frameNumber, DateTime timestamp, System.Collections.Specialized.NameValueCollection parameters) {
            string from = null;
            string to = null;
            string subject = null;
            string message = null;

            string[] fromNames = { "from", "From", "fFrom", "profile_id", "username", "guest_id", "author", "email", "anonName", "rawOpenId" };
            string[] toNames = { "to", "To", "req0_to", "fTo", "ids", "send_to", "emails[0]" };
            string[] subjectNames = { "subject", "Subj", "Subject", "fSubject" };
            string[] messageNames = { "req0_text", "body", "Body", "message", "Message", "text", "Text", "fMessageBody", "status", "PlainBody", "RichBody", "comment", "postBody" };

            /**
             * gmail emails uses "to", "subject" and "body"
             * gmail chat uses "req0_to" and "req0_text"
             * yahoo email uses "To", "Subj" and "Body" {
             * MS Exchange webmail uses "to", "subject" and "message"
             * others might use "from", "to", "subject" and "text"
             * Hotmail use "fFrom", "fTo", "fSubject" and "fMessageBody"
             * Facebook uses "profile_id", "ids", "subject"? and "status"
             * twitter uses "guest_id", ?, "status", "status"
             * AOL email parser uses PlainBody and RichBody
             * Squirrel Mail uses meddelande: username, send_to, subject, body
             * Wordpress uses author + email and comment
             * Blogspot uses anonName + rawOpenId and postBody
             */
            for (int i = 0; i < fromNames.Length && (from == null || from.Length == 0); i++)
                from = parameters[fromNames[i]];
            for (int i = 0; i < toNames.Length && (to == null || to.Length == 0); i++)
                to = parameters[toNames[i]];
            for (int i = 0; i < subjectNames.Length && (subject == null || subject.Length == 0); i++)
                subject = parameters[subjectNames[i]];
            for (int i = 0; i < messageNames.Length && (message == null || message.Length == 0); i++)
                message = parameters[messageNames[i]];

            if (subject == null && message != null && message.Length > 0)
                subject = message;
            if (subject != null && subject.Length > 0 && (from != null || to != null)) {
                long totalLength = subject.Length;
                if (from != null)
                    totalLength += from.Length;
                if (to != null)
                    totalLength += to.Length;
                if (message != null)
                    totalLength += message.Length;

                if (transferIsClientToServer)
                    return new PacketParser.Events.MessageEventArgs(applicationLayerProtocol, fiveTuple.ClientHost, fiveTuple.ServerHost, frameNumber, timestamp, from, to, subject, message, parameters, totalLength);
                else
                    return new PacketParser.Events.MessageEventArgs(applicationLayerProtocol, fiveTuple.ServerHost, fiveTuple.ClientHost, frameNumber, timestamp, from, to, subject, message, parameters, totalLength);
            }
            else
                return null;
        }

        private void CheckFrameCleartext(Frame frame) {
            int wordCharCount = 0;
            int totalByteCount = 0;
            IEnumerable<string> words = null;

            if (this.cleartextSearchModeSelectedIndex == 0) {//0 = full packet search
                words = GetCleartextWords(frame.Data);
                totalByteCount = frame.Data.Length;
            }
            else if (cleartextSearchModeSelectedIndex == 1) {//1 = TCP and UDP payload search
                foreach (Packets.AbstractPacket p in frame.PacketList/*.Values*/) {
                    if (p.GetType() == typeof(Packets.TcpPacket)) {
                        Packets.TcpPacket tcpPacket = (Packets.TcpPacket)p;
                        words = this.GetCleartextWords(tcpPacket.ParentFrame.Data, tcpPacket.PacketStartIndex + tcpPacket.DataOffsetByteCount, tcpPacket.PacketEndIndex);
                        totalByteCount = tcpPacket.PacketEndIndex - tcpPacket.DataOffsetByteCount - tcpPacket.PacketStartIndex + 1;
                    }
                    else if (p.GetType() == typeof(Packets.UdpPacket)) {
                        Packets.UdpPacket udpPacket = (Packets.UdpPacket)p;
                        words = this.GetCleartextWords(udpPacket.ParentFrame.Data, udpPacket.PacketStartIndex + udpPacket.DataOffsetByteCount, udpPacket.PacketEndIndex);
                        totalByteCount = udpPacket.PacketEndIndex - udpPacket.DataOffsetByteCount - udpPacket.PacketStartIndex + 1;
                    }
                }
            }
            else if (cleartextSearchModeSelectedIndex == 2) {//2 = raw packet search
                foreach (Packets.AbstractPacket p in frame.PacketList/*.Values*/) {
                    if (p.GetType() == typeof(Packets.RawPacket)) {
                        words = this.GetCleartextWords(p);
                        totalByteCount = p.PacketByteCount;
                    }
                }
            }
            //3 = don't search

            //add data to Form
            if (totalByteCount > 0 && words != null) {
                List<string> wordList = new List<string>();
                foreach (string cleartextWord in words) {
                    wordCharCount += cleartextWord.Length;
                    wordList.Add(cleartextWord);
                }
                if (wordList.Count > 0) {
                    this.OnCleartextWordsDetected(new Events.CleartextWordsEventArgs(wordList, wordCharCount, totalByteCount, frame.FrameNumber, frame.Timestamp));
                }
            }


        }

    }
}
