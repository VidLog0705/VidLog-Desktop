namespace VidLog.Desktop.Core.Cleanup;

/// <summary>
/// 「预留空间」的默认值（设计图 `_43` 那句话）。
/// </summary>
/// <remarks>
/// <para>
/// 设计图原话：「录像会按列表顺序保存，每个磁盘只剩下预留空间时，会自动换到下一个磁盘；
/// **系统盘默认至少预留 30GB 或 10%，其他磁盘至少预留 20GB 或 5%，以较大值为准**」。
/// </para>
/// <para>
/// ⚠️ <b>「以较大值为准」是**默认值的算法**，不是额外的硬下限</b> ——
/// 这一点由设计图上的数字**证实**：图里 D 盘显示 <c>28 GB</c>、C 盘 <c>30 GB</c>。
/// 若 D 是一块 560GB 的盘，<c>max(20GB, 560GB×5%) = 28GB</c> ✓；
/// 而 C 是系统盘，<c>max(30GB, 10%) = 30GB</c> ✓。两格都对得上。
/// </para>
/// <para>
/// ⚠️ 所以这里**不夹取用户填的值**：它只是「新建一条磁盘时预填多少」。
/// 用户把它改小是允许的（图上没禁止），`AppSettings.IsPlausible` 只挡负数那种明显打错的值。
/// </para>
/// </remarks>
public static class ReservedSpace
{
    /// <summary>系统盘的下限：30 GB。</summary>
    /// <remarks>
    /// ⚠️ 系统盘留多一点是**防事故**：Windows 把系统盘写满会出各种怪问题
    /// （分页文件写不出、更新失败、甚至起不来），而那些与这个程序无关，
    /// 用户不会想到是它造成的。
    /// </remarks>
    public const long SystemDriveFloorBytes = 30L * 1024 * 1024 * 1024;

    /// <summary>系统盘的比例下限：10%。</summary>
    public const double SystemDriveRatio = 0.10;

    /// <summary>其他盘的下限：20 GB。</summary>
    public const long OtherDriveFloorBytes = 20L * 1024 * 1024 * 1024;

    /// <summary>其他盘的比例下限：5%。</summary>
    public const double OtherDriveRatio = 0.05;

    /// <summary>
    /// 这块盘该预留多少（**默认值**）。<paramref name="isSystemDrive"/> 为真时按系统盘那两档算。
    /// </summary>
    /// <param name="totalBytes">
    /// 这块盘的总容量。⚠️ 拿不到时传 0 —— 那就只剩固定下限那一档
    /// （比例算不出来，而「宁可多留」是安全的那一头）。
    /// </param>
    public static long DefaultFor(bool isSystemDrive, long totalBytes)
    {
        var floor = isSystemDrive ? SystemDriveFloorBytes : OtherDriveFloorBytes;
        var ratio = isSystemDrive ? SystemDriveRatio : OtherDriveRatio;

        if (totalBytes <= 0)
        {
            return floor;
        }

        return Math.Max(floor, (long)(totalBytes * ratio));
    }
}
