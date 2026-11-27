namespace PcMieru.Party.Rendering;

/// <summary>
/// 基板のどの受け口に挿さるか。
/// 描画（BoardRenderer）と構成（Composition）の両方が使うので、WPF を持たない層に置く。
/// 名前空間は元のまま残してある（描画側の参照を書き換えずに済むように）。
/// </summary>
public enum SlotKind { Socket, Dimm, Pcie, M2, Sata, Atx }
