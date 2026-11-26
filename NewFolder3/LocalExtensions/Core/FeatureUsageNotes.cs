namespace DeepDungeon.Fsd.Core;

/// <summary>Read-only footer. Feature controls must always render before these notes.</summary>
public static class FeatureUsageNotes
{
    public static readonly string[] Items =
    [
        "輔助開關：勾選後進地宮即生效，不需啟動 FSD；出本、過圖或死亡時停用。",
        "移速含最大加速度，只在普通／最終獎勵層生效；GCD 會嘗試補招，完全停手請關閉。",
        "循環：完成目標才計次；失敗先續原檔，不能續才重開。保留存檔會關閉循環。",
        "續打與暫停：本內點「繼續探索」；組末暫停可再點取消。",
        "下底：領獎後自動退本，按模式與次數續跑；保檔、完成次數或要求暫停則停止。",
        "挖寶：合併重疊點盲踩；自由探索可限定層數，範圍外僅感知溢出時使用，準確點仍處理。",
        "強化／聚魔柄：完整層段算一輪；可選只刷強化、不兌換，道具按模式使用。",
        "觸發次數自動保存，重啟與更新不清零；同類輔助請勿重複開啟。"
    ];
}
