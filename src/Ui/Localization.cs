using Aprillz.MewUI;

namespace GeoJsonEditor.Ui;

/// <summary>
/// MewUI 内置控件的文字（输入框右键菜单、消息框按钮、忙碌遮罩、内置文件对话框等）默认是英文，
/// 启动时统一换成中文，与界面其余部分一致。
/// </summary>
public static class Localization
{
    public static void ApplyChinese()
    {
        MewUIStrings.CommonOK.Value = "确定";
        MewUIStrings.CommonCancel.Value = "取消";
        MewUIStrings.CommonYes.Value = "是";
        MewUIStrings.CommonNo.Value = "否";
        MewUIStrings.CommonRetry.Value = "重试";
        MewUIStrings.CommonIgnore.Value = "忽略";
        MewUIStrings.CommonAbort.Value = "中止";

        MewUIStrings.PromptInformation.Value = "提示";
        MewUIStrings.PromptWarning.Value = "警告";
        MewUIStrings.PromptError.Value = "错误";
        MewUIStrings.PromptQuestion.Value = "确认";
        MewUIStrings.PromptSuccess.Value = "完成";
        MewUIStrings.PromptShield.Value = "安全";
        MewUIStrings.PromptCrash.Value = "程序错误";
        MewUIStrings.PromptShowDetail.Value = "显示详细信息";

        MewUIStrings.BusyIndicatorAbortConfirmation.Value = "确定要中止这个操作吗？";
        MewUIStrings.BusyIndicatorAborting.Value = "正在中止…";

        MewUIStrings.CommandUndo.Value = "撤销";
        MewUIStrings.CommandRedo.Value = "重做";
        MewUIStrings.CommandCut.Value = "剪切";
        MewUIStrings.CommandCopy.Value = "复制";
        MewUIStrings.CommandPaste.Value = "粘贴";
        MewUIStrings.CommandDelete.Value = "删除";
        MewUIStrings.CommandSelectAll.Value = "全选";

        MewUIStrings.FileDialogTitleOpenSingle.Value = "打开文件";
        MewUIStrings.FileDialogTitleOpenMultiple.Value = "打开文件";
        MewUIStrings.FileDialogTitleSave.Value = "保存文件";
        MewUIStrings.FileDialogTitleSelectFolder.Value = "选择文件夹";
        MewUIStrings.FileDialogTitleFallback.Value = "文件";
        MewUIStrings.FileDialogAcceptOpen.Value = "打开";
        MewUIStrings.FileDialogAcceptSave.Value = "保存";
        MewUIStrings.FileDialogAcceptSelect.Value = "选择";
        MewUIStrings.FileDialogFileNameLabel.Value = "文件名：";
        MewUIStrings.FileDialogFileTypeLabel.Value = "文件类型：";
        MewUIStrings.FileDialogNavBack.Value = "后退";
        MewUIStrings.FileDialogNavForward.Value = "前进";
        MewUIStrings.FileDialogNavUp.Value = "上一级";
        MewUIStrings.FileDialogViewGrid.Value = "图标";
        MewUIStrings.FileDialogViewList.Value = "列表";
        MewUIStrings.FileDialogAllFiles.Value = "所有文件";
        MewUIStrings.FileDialogColumnName.Value = "名称";
        MewUIStrings.FileDialogColumnSize.Value = "大小";
        MewUIStrings.FileDialogColumnModified.Value = "修改日期";

        MewUIStrings.SidebarQuickAccess.Value = "快速访问";
        MewUIStrings.SidebarThisPC.Value = "此电脑";
        MewUIStrings.SidebarFavorites.Value = "个人收藏";
        MewUIStrings.SidebarLocations.Value = "位置";
        MewUIStrings.SidebarPlaces.Value = "位置";
        MewUIStrings.SidebarDevices.Value = "设备";

        MewUIStrings.FolderHome.Value = "主目录";
        MewUIStrings.FolderDesktop.Value = "桌面";
        MewUIStrings.FolderDownloads.Value = "下载";
        MewUIStrings.FolderDocuments.Value = "文稿";
        MewUIStrings.FolderPictures.Value = "图片";
        MewUIStrings.FolderMusic.Value = "音乐";
        MewUIStrings.FolderVideos.Value = "视频";
        MewUIStrings.FolderApplications.Value = "应用程序";

        MewUIStrings.ColorPickerHex.Value = "十六进制";
    }
}
