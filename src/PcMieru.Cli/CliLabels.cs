using PcMieru.Core;

namespace PcMieru.Cli;

/// <summary>
/// Plain-language names for the fields of the hardware model, used by <c>pcmi hw</c>. A field that has no entry here
/// is shown under its own (camelCase) name, so a field added later still appears. The JSON output never uses these.
/// </summary>
public static class CliLabels
{
    private static readonly (string Field, string Ja, string En)[] Table =
    [
        ("name", "名前", "Name"),
        ("vendor", "製造元", "Vendor"),
        ("family", "ファミリー", "Family"),
        ("model", "モデル", "Model"),
        ("stepping", "ステッピング", "Stepping"),
        ("physicalCores", "物理コア数", "Physical cores"),
        ("logicalProcessors", "論理プロセッサ数", "Logical processors"),
        ("performanceCores", "高性能コア数", "Performance cores"),
        ("efficiencyCores", "高効率コア数", "Efficiency cores"),
        ("baseClockMhz", "基本クロック", "Base clock"),
        ("socket", "ソケット", "Socket"),
        ("l1CacheBytes", "L1 キャッシュ", "L1 cache"),
        ("l2CacheBytes", "L2 キャッシュ", "L2 cache"),
        ("l3CacheBytes", "L3 キャッシュ", "L3 cache"),
        ("instructionSets", "命令セット", "Instruction sets"),
        ("totalBytes", "合計", "Total"),
        ("slotsTotal", "スロット数", "Slots"),
        ("slotsUsed", "使用中のスロット", "Slots in use"),
        ("maxCapacityBytes", "載せられる最大容量", "Maximum capacity"),
        ("modules", "メモリモジュール", "Memory module"),
        ("slot", "スロット", "Slot"),
        ("capacityBytes", "容量", "Capacity"),
        ("type", "種類", "Type"),
        ("speedMtPerSec", "速度", "Speed"),
        ("configuredSpeedMtPerSec", "動作速度", "Configured speed"),
        ("manufacturer", "製造元", "Manufacturer"),
        ("partNumber", "型番", "Part number"),
        ("serialNumber", "シリアル番号", "Serial number"),
        ("ranks", "ランク数", "Ranks"),
        ("voltageMillivolts", "電圧", "Voltage"),
        ("formFactor", "形状", "Form factor"),
        ("product", "製品名", "Product"),
        ("revision", "リビジョン", "Revision"),
        ("biosVendor", "BIOS の提供元", "BIOS vendor"),
        ("biosVersion", "BIOS のバージョン", "BIOS version"),
        ("biosDate", "BIOS の日付", "BIOS date"),
        ("uefiBoot", "UEFI で起動", "UEFI boot"),
        ("secureBoot", "セキュアブート", "Secure Boot"),
        ("tpmPresent", "TPM", "TPM present"),
        ("tpmVersion", "TPM のバージョン", "TPM version"),
        ("driverVersion", "ドライバーの版", "Driver version"),
        ("driverDate", "ドライバーの日付", "Driver date"),
        ("pciId", "PCI ID", "PCI ID"),
        ("luid", "LUID", "LUID"),
        ("dedicatedBytes", "専用メモリ", "Dedicated memory"),
        ("sharedBytes", "共有メモリ", "Shared memory"),
        ("vbios", "VBIOS", "VBIOS"),
        ("pcieGeneration", "PCIe の世代", "PCIe generation"),
        ("pcieWidth", "PCIe のレーン数", "PCIe lanes"),
        ("powerLimitWatts", "電力の上限", "Power limit"),
        ("physicalDriveNumber", "ドライブ番号", "Drive number"),
        ("firmware", "ファームウェア", "Firmware"),
        ("bus", "接続方式", "Bus"),
        ("trimSupported", "TRIM 対応", "TRIM supported"),
        ("partitionStyle", "パーティション形式", "Partition style"),
        ("volumes", "ボリューム", "Volume"),
        ("letter", "ドライブ文字", "Letter"),
        ("label", "ラベル", "Label"),
        ("fileSystem", "ファイルシステム", "File system"),
        ("freeBytes", "空き", "Free"),
        ("kind", "種類", "Kind"),
        ("linkSpeedBitsPerSec", "リンク速度", "Link speed"),
        ("macAddress", "MAC アドレス", "MAC address"),
        ("ipAddresses", "IP アドレス", "IP addresses"),
        ("dnsServers", "DNS サーバー", "DNS servers"),
        ("ssid", "SSID", "SSID"),
        ("wifiBand", "Wi-Fi の帯域", "Wi-Fi band"),
        ("signalPercent", "電波の強さ", "Signal strength"),
        ("manufactureYear", "製造年", "Manufacture year"),
        ("manufactureWeek", "製造週", "Manufacture week"),
        ("physicalWidthMm", "画面の幅", "Width"),
        ("physicalHeightMm", "画面の高さ", "Height"),
        ("preferredResolution", "推奨の解像度", "Recommended resolution"),
        ("currentResolution", "いまの解像度", "Current resolution"),
        ("refreshHz", "リフレッシュレート", "Refresh rate"),
        ("hdrEnabled", "HDR", "HDR"),
        ("connection", "つなぎ方", "Connection"),
        ("gpuName", "つながっている GPU", "Connected GPU"),
        ("direction", "向き", "Direction"),
        ("isDefault", "既定の機器", "Default device"),
        ("sampleRateHz", "サンプルレート", "Sample rate"),
        ("bitsPerSample", "ビット深度", "Bit depth"),
        ("vendorId", "ベンダー ID", "Vendor ID"),
        ("productId", "製品 ID", "Product ID"),
        ("parentId", "親の ID", "Parent ID"),
        ("chargePercent", "充電量", "Charge"),
        ("onAc", "AC 電源に接続", "On AC power"),
        ("designCapacityMilliwattHours", "設計上の容量", "Design capacity"),
        ("fullChargeCapacityMilliwattHours", "満充電の容量", "Full-charge capacity"),
        ("wearPercent", "劣化", "Wear"),
        ("cycleCount", "充放電の回数", "Cycle count"),
        ("version", "バージョン", "Version"),
        ("build", "ビルド", "Build"),
        ("architecture", "アーキテクチャ", "Architecture"),
        ("uptimeSec", "稼働時間", "Uptime"),
        ("powerPlan", "電源プラン", "Power plan"),
        ("virtualizationBasedSecurity", "仮想化ベースのセキュリティ", "Virtualization-based security"),
        ("hvci", "メモリ整合性（HVCI）", "Memory integrity (HVCI)"),
    ];

    private static readonly Dictionary<string, (string Ja, string En)> ByField =
        Table.ToDictionary(t => t.Field, t => (t.Ja, t.En), StringComparer.Ordinal);

    public static IEnumerable<StringEntry> Entries => Table.Select(t => new StringEntry("cli.field." + t.Field, t.Ja, t.En));

    public static bool Has(string field) => ByField.ContainsKey(field);

    /// <summary>The plain-language name, or the field's own name when none is registered.</summary>
    public static string Label(string field, Language language) =>
        ByField.TryGetValue(field, out var text) ? (language == Language.Ja ? text.Ja : text.En) : field;
}
