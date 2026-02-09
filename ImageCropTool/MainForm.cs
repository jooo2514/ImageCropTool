using OpenCvSharp;
using OpenCvSharp.Extensions;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

// 타입 충돌 방지
using DPoint = System.Drawing.Point;

namespace ImageCropTool
{
    public partial class MainForm : Form
    {
        /* ================= Image ================= */
        private Bitmap originalBitmap = null;
        private Bitmap viewBitmap = null;
        private Mat originalMat = null;

        private string imageColorInfoText = string.Empty;

        /* ================= View ================= */
        private float viewScale = 1f;
        private PointF viewOffset = PointF.Empty;
        private bool isPanning = false;
        private DPoint lastMousePt;

        private const float ZoomStep = 1.1f;
        private const float MinZoom = 0.5f;
        private const float MaxZoom = 3.0f;

        /* ================= GuideLines ================= */
        private readonly List<GuideLineInfo> guideLines = new List<GuideLineInfo>();
        private readonly Dictionary<GuideLineInfo, List<CropBoxInfo>> cropBoxMap
            = new Dictionary<GuideLineInfo, List<CropBoxInfo>>();

        private GuideLineInfo currentLine = null;
        private CropBoxInfo hoveredBox = null;   // 지금 hover중인 대상
        private GuideLineInfo hoveredLine = null;


        /* ================= Mouse ================= */
        private PointF mouseOriginalPt = PointF.Empty;
        private DPoint mouseScreenPt;      // 텍스트를 그릴 화면 위치

        /* ================= Drag State ================= */
        private GuideLineInfo dragLine;
        private bool draggingStart;
        private bool draggingEnd;
        private const int HitRadius = 8;

        /* =========== Loading Spinner ===============*/
        private bool isImageLoading = false;
        private Timer loadingTimer;
        private float spinnerAngle = 0f;


        public MainForm()
        {
            InitializeComponent();

            loadingTimer = new Timer { Interval = 50 };
            loadingTimer.Tick += (s, e) =>
            {
                spinnerAngle = (spinnerAngle + 20) % 360;
                pictureBoxImage.Invalidate();
            };


            pictureBoxImage.SizeMode = PictureBoxSizeMode.Normal;
            pictureBoxImage.Paint += PictureBoxImage_Paint;
            pictureBoxImage.MouseDown += PictureBoxImage_MouseDown;
            pictureBoxImage.MouseMove += PictureBoxImage_MouseMove;
            pictureBoxImage.MouseUp += PictureBoxImage_MouseUp;
            pictureBoxImage.MouseWheel += PictureBoxImage_MouseWheel;

            pictureBoxPreview.SizeMode = PictureBoxSizeMode.Zoom;
            numCropSize.ValueChanged += NumCropSize_ValueChanged;
        }

        /* ================= Reset ================= */
        private void BtnReset_Click(object sender, EventArgs e) => ResetAll();

        private void ResetAll()
        {
            guideLines.Clear();
            cropBoxMap.Clear();
            currentLine = null;
            ResetView();
            pictureBoxImage.Invalidate();
        }

        private Bitmap ResizeToFit(Bitmap src, int maxW, int maxH)
        {
            float scale = Math.Min((float)maxW / src.Width, (float)maxH / src.Height);
            int w = (int)(src.Width * scale);
            int h = (int)(src.Height * scale);

            Bitmap dst = new Bitmap(w, h);
            using (Graphics g = Graphics.FromImage(dst))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(src, 0, 0, w, h);
            }
            return dst;
        }

        private void ResetView()
        {
            if (viewBitmap == null) return;

            viewScale = 1f;
            viewOffset = new PointF(
                (pictureBoxImage.Width - viewBitmap.Width) / 2f,
                (pictureBoxImage.Height - viewBitmap.Height) / 2f
            );
        }


        /* ================= Load Image ================= */
        private async void BtnLoadImage_Click(object sender, EventArgs e)
        {
            OpenFileDialog dlg = new OpenFileDialog
            {
                Filter = "Image Files|*.bmp;*.jpg;*.png"
            };

            if (dlg.ShowDialog() != DialogResult.OK)
                return;

            isImageLoading = true;
            loadingTimer.Start();

            pictureBoxImage.Enabled = false;
            btnCropSave.Enabled = false;
            btnLoadImage.Enabled = false;
            btnReset.Enabled = false;

            try
            {
                await Task.Run(() =>
                {
                    using (Bitmap full = new Bitmap(dlg.FileName))
                    {
                        viewBitmap = ResizeToFit(
                            full,
                            pictureBoxImage.Width,
                            pictureBoxImage.Height
                        );
                    }

                    originalBitmap?.Dispose();
                    originalMat?.Dispose();

                    originalBitmap = new Bitmap(dlg.FileName);
                    originalMat = BitmapConverter.ToMat(originalBitmap);  // 연산용

                    // 이미지 타입 판별
                    if (originalMat.Channels() == 1)
                        imageColorInfoText = "Grayscale (CV_8UC1)";
                    else if (originalMat.Channels() == 3)
                        imageColorInfoText = "Color (CV_8UC3)";
                    else
                        imageColorInfoText = $"Channels: {originalMat.Channels()}";
                });

                ResetAll();
            }
            catch (Exception ex)
            {
                MessageBox.Show("이미지 로드 실패: " + ex.Message);
            }
            finally
            {
                isImageLoading = false;
                loadingTimer.Stop();

                pictureBoxImage.Enabled = true;
                btnCropSave.Enabled = true;
                btnLoadImage.Enabled = true;
                btnReset.Enabled = true;

                pictureBoxImage.Invalidate();
            }
        }


        /* ================= Mouse ================= */
        private void PictureBoxImage_MouseDown(object sender, MouseEventArgs e)
        {
            if (viewBitmap == null || originalBitmap == null)
                return;

            if (!IsInsideImageScreen(e.Location))
            {
                MessageBox.Show(
                    "이미지 영역 안을 클릭하세요.",
                    "잘못된 클릭",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning
                );
                return;
            }
           
            switch (e.Button)
            {
                case MouseButtons.Right:
                    isPanning = true;
                    lastMousePt = e.Location;
                    return;
                case MouseButtons.Left:
                    {
                        PointF viewPt = ScreenToView(e.Location);   //  View 좌표
                        PointF OriginalPt = ViewToOriginal(viewPt); //  Original 좌표

                        foreach (GuideLineInfo line in guideLines)
                        {
                            PointF startView = OriginalToView(line.StartPt);
                            PointF endView = OriginalToView(line.EndPt);

                            if (Hit(startView, viewPt))
                            {
                                dragLine = line;
                                draggingStart = true;
                                return;
                            }
                            if (Hit(endView, viewPt))
                            {
                                dragLine = line;
                                draggingEnd = true;
                                return;
                            }
                        }

                        // 새 라인 생성
                        if (currentLine == null)
                        {
                            currentLine = new GuideLineInfo();
                            currentLine.StartPt = OriginalPt; // 🔴 반드시 Original
                            currentLine.CropSize = (int)numCropSize.Value;
                            currentLine.Anchor = CropAnchor.Center;
                        }
                        else
                        {
                            currentLine.EndPt = OriginalPt;   // 🔴 반드시 Original
                            guideLines.Add(currentLine);

                            cropBoxMap[currentLine] = new List<CropBoxInfo>();
                            CalculateCropBoxes(currentLine);

                            currentLine = null;
                        }

                        pictureBoxImage.Invalidate();
                        break;
                    }

            }
        }


        private void PictureBoxImage_MouseMove(object sender, MouseEventArgs e)
        {
            if (viewBitmap == null || originalBitmap == null)
                return;

            if (isPanning)
            {
                viewOffset.X += e.X - lastMousePt.X;
                viewOffset.Y += e.Y - lastMousePt.Y;
                lastMousePt = e.Location;
                pictureBoxImage.Invalidate();
                return;
            }

            PointF originalPt = ViewToOriginal(ScreenToView(e.Location));
            mouseOriginalPt = originalPt;


            if (dragLine != null)
            {
                if (draggingStart) dragLine.StartPt = originalPt;
                if (draggingEnd) dragLine.EndPt = originalPt;
                CalculateCropBoxes(dragLine);

                UpdateLineInfo(dragLine);
                pictureBoxImage.Invalidate();
                return;
            }
            mouseScreenPt = e.Location;   // 화면에 좌표 표시용
            mouseOriginalPt = ViewToOriginal(ScreenToView(e.Location));
            UpdateHoverPreview(originalPt);
            pictureBoxImage.Invalidate();

        }

        private void PictureBoxImage_MouseUp(object sender, MouseEventArgs e)
        {
            isPanning = false;
            draggingStart = draggingEnd = false;
            dragLine = null;
        }

        private void PictureBoxImage_MouseWheel(object sender, MouseEventArgs e)
        {
            if (viewBitmap == null) return;

            float oldScale = viewScale;
            viewScale = e.Delta > 0 ? viewScale * ZoomStep : viewScale / ZoomStep;
            viewScale = Math.Max(MinZoom, Math.Min(MaxZoom, viewScale));

            viewOffset.X = e.X - (e.X - viewOffset.X) * (viewScale / oldScale);
            viewOffset.Y = e.Y - (e.Y - viewOffset.Y) * (viewScale / oldScale);

            pictureBoxImage.Invalidate();
        }

        /* ================= Paint ================= */
        private void PictureBoxImage_Paint(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Color.Black);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            if (isImageLoading)
            {
                DrawLoadingSpinner(g);
                return;
            }
            if (viewBitmap == null)
                return;

            g.TranslateTransform(viewOffset.X, viewOffset.Y);
            g.ScaleTransform(viewScale, viewScale);
            g.DrawImage(viewBitmap, 0, 0);

            foreach (GuideLineInfo line in guideLines)
            {
                DrawGuideLine(g, line);
                DrawGuideBoxes(g, line);
            }

            if (currentLine != null)
                DrawTempLine(g, currentLine);
           

            g.ResetTransform();
            DrawMousePositionOverlay(g);
            DrawImageTypeOverlay(g);
        }

        /* ============= Draw Helpers ================== */
        private void DrawTempLine(Graphics g, GuideLineInfo line)
        {
            using (Pen pen = new Pen(Color.Lime, 2 / viewScale))
            {
                g.DrawLine(pen,
                    OriginalToView(line.StartPt),
                    OriginalToView(mouseOriginalPt));
            }
        }
        private void DrawGuideLine(Graphics g, GuideLineInfo line)
        {
            using (Pen pen = new Pen(Color.Red, 2 / viewScale))
            {
                g.DrawLine(pen,
                    OriginalToView(line.StartPt),
                    OriginalToView(line.EndPt));
            }
        }

        private void DrawGuideBoxes(Graphics g, GuideLineInfo line)
        {
            if (!cropBoxMap.TryGetValue(line, out var boxes))
                return;

            foreach (CropBoxInfo box in boxes)
            {
                Rectangle r = box.Rect;

                PointF tl = OriginalToView(new PointF(r.Left, r.Top));
                PointF br = OriginalToView(new PointF(r.Right, r.Bottom));

                Color color = box.IsHovered ? Color.Lime : Color.Yellow;

                using (Pen pen = new Pen(color, 2 / viewScale))
                {
                    pen.DashStyle = DashStyle.Dash;
                    g.DrawRectangle(
                        pen,
                        tl.X,
                        tl.Y,
                        br.X - tl.X,
                        br.Y - tl.Y
                    );
                }
            }
        }


        private void DrawImageTypeOverlay(Graphics g)
        {
            if (string.IsNullOrEmpty(imageColorInfoText)) return;

            using (Font font = new Font("맑은 고딕", 9, FontStyle.Bold))
            {
                SizeF size = g.MeasureString(imageColorInfoText, font);
                RectangleF bg = new RectangleF(8, 8, size.Width + 8, size.Height + 8);

                using (Brush b = new SolidBrush(Color.FromArgb(160, 0, 0, 0)))
                    g.FillRectangle(b, bg);

                g.DrawString(imageColorInfoText, font, Brushes.Orange, 12, 11);
            }
        }
        private void DrawMousePositionOverlay(Graphics g)   // 마우스 포지션 점좌표 그리기
        {
            string text = $"({(int)mouseOriginalPt.X}, {(int)mouseOriginalPt.Y})";

            using (Font font = new Font("맑은 고딕", 9, FontStyle.Bold))
            {
                SizeF size = g.MeasureString(text, font);
                float x = mouseScreenPt.X + 12;      // 마우스 커서랑 겹치지 않게
                float y = mouseScreenPt.Y + 12;

                RectangleF bg = new RectangleF(     // 배경 사각형 크기
                    x, y,
                    size.Width + 8,
                    size.Height + 8
                );
                // 반투명 배경
                //using (Brush b = new SolidBrush(Color.FromArgb(180, 0, 0, 0)))
                //    g.FillRectangle(b, bg);

                g.DrawString(text, font, Brushes.DeepSkyBlue, x + 4, y + 4);    // 텍스트
            }
        }
        /* ================= Crop ================= */
        private void CalculateCropBoxes(GuideLineInfo line)
        {
            List<CropBoxInfo> boxes = cropBoxMap[line];
            boxes.Clear();

            float dx = line.EndPt.X - line.StartPt.X;
            float dy = line.EndPt.Y - line.StartPt.Y;
            float length = (float)Math.Sqrt(dx * dx + dy * dy);
            if (length < 1f) return;

            float ux = dx / length;
            float uy = dy / length;

            for (float d = 0; d <= length + line.CropSize / 2f; d += line.CropSize)
            {
                float cx = line.StartPt.X + ux * d;
                float cy = line.StartPt.Y + uy * d;

                int x = (int)(cx - line.CropSize / 2f);
                int y = (int)(cy - line.CropSize / 2f);

                x = Math.Max(0, Math.Min(x, originalMat.Width - line.CropSize));
                y = Math.Max(0, Math.Min(y, originalMat.Height - line.CropSize));

                boxes.Add(new CropBoxInfo
                {
                    Rect = new Rectangle(x, y, line.CropSize, line.CropSize),
                    OwnerLine = line,       // ⭐ 여기
                    IsHovered = false
                });
            }
        }


        /* ================= Save ================= */
        private void BtnCropSave_Click(object sender, EventArgs e)
        {
            if (guideLines.Count == 0)
                return;

            string folder = Path.Combine(
                Application.StartupPath,
                "Crops",
                DateTime.Now.ToString("yyyyMMdd_HHmmss")
            );

            Directory.CreateDirectory(folder);
            int index = 1;

            foreach (GuideLineInfo line in guideLines)
            {
                foreach (CropBoxInfo box in cropBoxMap[line])
                {
                    Rectangle r = box.Rect;
                    using (Mat cropped = new Mat(
                        originalMat,
                        new OpenCvSharp.Rect(r.X, r.Y, r.Width, r.Height)))
                    {
                        Cv2.ImWrite(
                            Path.Combine(folder, string.Format("crop_{0:D3}.png", index++)),
                            cropped
                        );
                    }
                }
            }

            MessageBox.Show("저장 완료");
        }

        /* ================= Utils ================= */
        private bool Hit(PointF viewA, PointF viewB)
        {
            float dx = viewA.X - viewB.X;
            float dy = viewA.Y - viewB.Y;
            return dx * dx + dy * dy <= HitRadius * HitRadius;
        }

        private PointF ScreenToView(DPoint pt)
        {
            return new PointF(
                (pt.X - viewOffset.X) / viewScale,
                (pt.Y - viewOffset.Y) / viewScale
            );
        }

        private PointF ViewToOriginal(PointF pt)
        {
            if (originalBitmap == null || viewBitmap == null)
                return PointF.Empty;

            return new PointF(
                pt.X * originalBitmap.Width / viewBitmap.Width,
                pt.Y * originalBitmap.Height / viewBitmap.Height
            );
        }

        private PointF OriginalToView(PointF pt)
        {
            if (originalBitmap == null || viewBitmap == null)
                return PointF.Empty;

            return new PointF(
                pt.X * viewBitmap.Width / originalBitmap.Width,
                pt.Y * viewBitmap.Height / originalBitmap.Height
            );
        }

        private bool IsInsideImageScreen(DPoint pt)
        {
            if (viewBitmap == null) return false;

            RectangleF rect = new RectangleF(
                viewOffset.X,
                viewOffset.Y,
                viewBitmap.Width * viewScale,
                viewBitmap.Height * viewScale
            );
            return rect.Contains(pt);
        }

        private void NumCropSize_ValueChanged(object sender, EventArgs e)
        {
            foreach (GuideLineInfo line in guideLines)
            {
                line.CropSize = (int)numCropSize.Value;
                CalculateCropBoxes(line);
            }
            pictureBoxImage.Invalidate();
        }


        /* ================= Preview ================= */
        private void UpdateHoverPreview(PointF originalPt)
        {
            hoveredBox = null;
            hoveredLine = null;

            foreach (var pair in cropBoxMap)
            {
                GuideLineInfo line = pair.Key;
                List<CropBoxInfo> boxes = pair.Value;

                foreach (var box in boxes)
                {
                    if (box.Rect.Contains(
                        (int)originalPt.X,
                        (int)originalPt.Y))
                    {
                        hoveredBox = box;
                        hoveredLine = line;
                        break;
                    }
                }
                if (hoveredBox != null)
                    break;
            }

            // hover 상태 갱신
            foreach (var pair in cropBoxMap)
                foreach (var box in pair.Value)
                    box.IsHovered = (box == hoveredBox);

            if (hoveredBox != null)
            {
                ShowCropPreview(hoveredBox);
                UpdateLineInfo(hoveredLine);   // ⭐ 여기!!
            }
            else
            {
                ClearPreview();
                ClearLineInfo();
            }

            pictureBoxImage.Invalidate();
        }

        private void ClearPreview()
        {
            pictureBoxPreview.Image?.Dispose();
            pictureBoxPreview.Image = null;
        }

        private void ClearLineInfo()
        {
            lblLineLength.Text = "Line Length: -";
            lblCropCount.Text = "Crop Count: -";
        }


        private void ShowCropPreview(CropBoxInfo hoverdBox)   // 박스 미리보기
        {
            if (hoverdBox == null || originalMat == null)
                return;

            Rectangle r = hoverdBox.Rect;

            var roi = new OpenCvSharp.Rect(   // ROI 생성
                r.X, r.Y, r.Width, r.Height
            );

            using (Mat cropped = new Mat(originalMat, roi))   // ROI로 Mat 잘라내기
            {
                pictureBoxPreview.Image?.Dispose();
                pictureBoxPreview.Image = BitmapConverter.ToBitmap(cropped);
            }
        }

        /* ===============  UI ====================== */
        private void UpdateLineInfo(GuideLineInfo line)
        {
            if (line == null || !cropBoxMap.ContainsKey(line))
            {
                ClearLineInfo();
                return;
            }

            float dx = line.EndPt.X - line.StartPt.X;
            float dy = line.EndPt.Y - line.StartPt.Y;
            float length = (float)Math.Sqrt(dx * dx + dy * dy);

            int cropCount = cropBoxMap[line].Count;

            lblLineLength.Text = $"Line Length: {length:F1}px";
            lblCropCount.Text = $"Crop Count: {cropCount}";
        }



        private void DrawLoadingSpinner(Graphics g)
        {
            int size = 50;
            int x = (pictureBoxImage.Width - size) / 2;
            int y = (pictureBoxImage.Height - size) / 2;

            using (Pen bg = new Pen(Color.FromArgb(50, Color.Gray), 6))
            using (Pen fg = new Pen(Color.DeepSkyBlue, 6))
            {
                g.DrawEllipse(bg, x, y, size, size);
                g.DrawArc(fg, x, y, size, size, spinnerAngle, 100);
            }

            using (Font font = new Font("맑은 고딕", 10, FontStyle.Bold))
            {
                string msg = "Loading...";
                SizeF ts = g.MeasureString(msg, font);
                g.DrawString(
                    msg,
                    font,
                    Brushes.DimGray,
                    (pictureBoxImage.Width - ts.Width) / 2,
                    y + size + 10
                );
            }
        }

    }
}
