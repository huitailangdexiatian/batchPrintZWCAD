using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
#if AUTOCAD
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
#if ACAD_CORE
using CadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
#else
using CadApp = Autodesk.AutoCAD.ApplicationServices.Application;
#endif
#else
using ZwSoft.ZwCAD.ApplicationServices;
using ZwSoft.ZwCAD.DatabaseServices;
using ZwSoft.ZwCAD.EditorInput;
using ZwSoft.ZwCAD.Geometry;
using CadApp = ZwSoft.ZwCAD.ApplicationServices.Application;
#endif

namespace ZwcadBatchPlot;

public sealed partial class BatchPlotCommands
{
    /// <summary>
    /// 新增属性图框：图名、图号等字段值不框选文字区域，
    /// 扫描时按设置的关键字从块属性（Attribute）自动提取。
    /// </summary>
    private static void AddAttributeTitleBlockCore()
    {
        var doc = CadApp.DocumentManager.MdiActiveDocument;
        if (doc == null)
        {
            AddBlockLog("No active document (attribute title block).");
            return;
        }

        var editor = doc.Editor;

        // 与普通新增图框一致：必须在 WCS 下操作，避免坐标区域错位。
        if (!editor.CurrentUserCoordinateSystem.IsEqualTo(Matrix3d.Identity))
        {
            MessageBox.Show("新增属性图框前请先将 UCS 切换为世界坐标系（WCS）。\n命令行输入 UCS 然后回车即可。",
                "批量打印", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        AddBlockLog("Add attribute title block command started.");

        try
        {
            var blockOptions = new PromptEntityOptions("\n选择要加入图框库的属性图框块: ");
            blockOptions.SetRejectMessage("\n请选择块参照。");
            blockOptions.AddAllowedClass(typeof(BlockReference), exactMatch: false);
            var blockResult = editor.GetEntity(blockOptions);
            AddBlockLog("Block prompt status: " + blockResult.Status);
            if (blockResult.Status != PromptStatus.OK)
            {
                return;
            }

            string blockName;
            Matrix3d blockTransform;
            ObjectId frameDefinitionId;
            bool isPaperSpace;
            bool isStretchableBlock;
            List<(string Tag, string Value)> attributes;
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var blockRef = (BlockReference)tr.GetObject(blockResult.ObjectId, OpenMode.ForRead);
                blockName = CadTextExtractor.GetLibraryIdentityName(blockRef, tr);
                blockTransform = blockRef.BlockTransform;
                frameDefinitionId = blockRef.BlockTableRecord;
                isStretchableBlock = HasStretchDistanceProperty(blockRef) || HasLookupStretchProperty(blockRef);

                // 属性图框的核心要求：块参照必须带属性，否则没有可提取的字段值。
                attributes = AttributeTitleBlockFieldExtractor.ReadAttributes(tr, blockRef);
                if (attributes.Count == 0)
                {
                    tr.Commit();
                    editor.WriteMessage("\n所选块不包含任何属性（Attribute），无法作为属性图框录入。");
                    MessageBox.Show(
                        "所选块不包含任何属性（Attribute），无法作为属性图框录入。\n请选择带属性的图框块，或使用“新增图框”按区域框选录入。",
                        "批量打印", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // 纸张候选顺序与普通图框一致：模型空间优先 1:100，布局空间优先 1:1。
                var owner = (BlockTableRecord)tr.GetObject(blockRef.OwnerId, OpenMode.ForRead);
                isPaperSpace = owner.IsLayout
                    && !owner.LayoutId.IsNull
                    && !((Layout)tr.GetObject(owner.LayoutId, OpenMode.ForRead)).ModelType;

                tr.Commit();
            }

            AddBlockLog($"Selected attribute block: {blockName}, attributes={attributes.Count}");

            // 同名图框允许覆盖重录；用户拒绝时不继续。
            var existingLibrary = TitleBlockLibraryStore.Load();
            if (existingLibrary.Blocks.Any(x =>
                    string.Equals(x.BlockName, blockName, StringComparison.OrdinalIgnoreCase)))
            {
                var overwrite = MessageBox.Show(
                    $"图框库中已存在同名图框: {blockName}\n是否重新录入？\n选择“是”将覆盖原有图框设置。",
                    "批量打印",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button2);
                if (overwrite != DialogResult.Yes)
                {
                    AddBlockLog("Duplicate block name; user chose not to overwrite (attribute).");
                    editor.WriteMessage($"\n已取消录入，图框库中的 {blockName} 保持不变。");
                    return;
                }
            }

            var inverse = blockTransform.Inverse();

            // 打印外框识别：优先块内最大闭合矩形，其次可见线包围盒，最后手动框选。
            Extents3d printExtents;
            LocalRectangle referenceFrame;
            if (BlockFrameGeometry.TryGetFrame(
                    doc.Database,
                    frameDefinitionId,
                    out referenceFrame,
                    out var frameSource))
            {
                printExtents = TransformRegion(referenceFrame, blockTransform);
                AddBlockLog(frameSource == BlockFrameSource.ClosedRectangle
                    ? "Outer frame detected from the largest closed rectangle inside block."
                    : "No closed rectangle found; outer frame detected from visible line geometry extents.");
            }
            else if (TryGetBlockExtents(doc.Database, blockResult.ObjectId, out var blockExtents))
            {
                printExtents = blockExtents;
                referenceFrame = TransformExtents(blockExtents, inverse);
            }
            else
            {
                AddBlockLog("Block geometric extents are invalid; requiring an explicit print boundary.");
                MessageBox.Show(
                    "CAD 无法取得该图框块的有效外包框，请手动框选打印边界。",
                    "批量打印",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);

                if (!TryGetRegion(
                        editor,
                        "\n框选图框打印外边界第一个角点: ",
                        "\n框选图框打印外边界对角点: ",
                        inverse,
                        out var manualFrame))
                {
                    AddBlockLog("Required print boundary selection cancelled (attribute).");
                    return;
                }

                referenceFrame = manualFrame;
                printExtents = TransformRegion(manualFrame, blockTransform);
            }

            // 外框红色临时标识；对话框内可重新框选修改打印范围。
            using var markers = new TransientFrameMarkers(editor);
            markers.SetBox("外框", printExtents.MinPoint, printExtents.MaxPoint, null);
            editor.WriteMessage("\n已自动识别图框外框（红色临时标识），请在弹出窗口中确认纸张并核对属性关键字命中情况。");

            var placedFrame = RectangleGeometry.TransformRectangle(referenceFrame, blockTransform);
            var detectedWidth = placedFrame.ActualWidth > 0
                ? placedFrame.ActualWidth
                : printExtents.MaxPoint.X - printExtents.MinPoint.X;
            var detectedHeight = placedFrame.ActualHeight > 0
                ? placedFrame.ActualHeight
                : printExtents.MaxPoint.Y - printExtents.MinPoint.Y;
            var settings = AppSettingsStore.Load();
            var paperDetectionOptions = PaperSizeDetector.CreateRectangleBatchOptions(
                settings.PaperMatchToleranceMm,
                isPaperSpace,
                settings.LongPaperSnapToleranceMm,
                settings.CustomScales);
            paperDetectionOptions.IncludeGenericDynamicTitleBlockPaper = isStretchableBlock;
            var paperOptions = ArbitraryPaperPicker.DetectCandidatesOrPrompt(
                detectedWidth,
                detectedHeight,
                paperDetectionOptions);
            var detected = paperOptions[0];

            AddBlockLog($"Detected {paperOptions.Count} paper option(s); preferred: {detected.PaperName}, {detected.PaperWidthMm:0.##} x {detected.PaperHeightMm:0.##}, {detected.ScaleText}");

            // 属性预览：标签 + 当前值 + 按关键字命中的字段名（绿色加粗标注）。
            var previewItems = attributes
                .Select(a =>
                {
                    var matchedFieldKey = AttributeTitleBlockFieldExtractor.FindMatchedFieldKey(
                        a.Tag,
                        settings.AttributeTitleBlockKeywords);
                    return new AttributePreviewItem(
                        a.Tag,
                        a.Value,
                        matchedFieldKey == null ? "" : AppSettingsStore.GetAttributeFieldDisplayName(matchedFieldKey));
                })
                .ToList();

            string paperName;
            double paperWidthMm;
            double paperHeightMm;
            using (var fieldDialog = new FieldBoxSelectDialog(
                       editor,
                       inverse,
                       blockTransform,
                       markers,
                       referenceFrame,
                       paperOptions,
                       paperDetectionOptions,
                       initialState: null,
                       attributePreview: previewItems))
            {
                if (ShowModalDialog(fieldDialog) != DialogResult.OK)
                {
                    AddBlockLog("Attribute field dialog cancelled.");
                    return;
                }

                referenceFrame = fieldDialog.ReferenceFrame;
                paperName = fieldDialog.PaperName;
                paperWidthMm = fieldDialog.PaperWidthMm;
                paperHeightMm = fieldDialog.PaperHeightMm;
            }

            var now = DateTime.Now;
            var definition = new TitleBlockDefinition
            {
                BlockName = blockName,
                IsAttributeBased = true,
                HasPrintRegion = true,
                CoordinateMode = isStretchableBlock
                    ? TitleBlockDefinition.DynamicRightBottomCoordinateMode
                    : "Frame",
                PrintRegion = referenceFrame,
                PaperName = paperName,
                PaperWidthMm = paperWidthMm,
                PaperHeightMm = paperHeightMm,
                CreatedAt = now,
                UpdatedAt = now
            };

            var inserted = TitleBlockLibraryStore.Upsert(definition);
            var saved = TitleBlockLibraryStore.Load();
            var savedDefinition = saved.Blocks.FirstOrDefault(x =>
                string.Equals(x.BlockName, blockName, StringComparison.OrdinalIgnoreCase));

            AddBlockLog($"Saved attribute title block. inserted={inserted}, libraryCount={saved.Blocks.Count}, verifyFound={savedDefinition != null}, path={TitleBlockLibraryStore.DefaultPath}");
            if (savedDefinition == null)
            {
                throw new InvalidOperationException("图框库保存后回读验证失败，请检查配置文件权限。");
            }

            editor.WriteMessage(inserted
                ? $"\n已新增属性图框块: {blockName}"
                : $"\n已更新已有属性图框块: {blockName}");
            editor.WriteMessage(isStretchableBlock
                ? $"\n可拉伸基础图幅: {definition.PaperName}（实际纸张在扫描块实例时判断）"
                : $"\n固定输出纸张: {definition.PaperName} {definition.PaperWidthMm:0.##} x {definition.PaperHeightMm:0.##} mm");
            editor.WriteMessage("\n字段值将按关键字从块属性提取，关键字可在“批量打印设置 → 属性图框设置”中修改。");
            editor.WriteMessage($"\n图框库: {TitleBlockLibraryStore.DefaultPath}");

            MessageBox.Show(
                $"属性图框已保存: {blockName}\n属性数: {attributes.Count}\n纸张: {paperName} {paperWidthMm:0.##} x {paperHeightMm:0.##} mm",
                "批量打印",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (System.Exception ex)
        {
            AddBlockLog("Add attribute title block failed: " + ex);
            editor.WriteMessage("\n新增属性图框失败: " + ex.Message);
            MessageBox.Show("新增属性图框失败: " + ex.Message, "批量打印", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
