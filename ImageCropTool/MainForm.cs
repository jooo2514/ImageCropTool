using OpenCvSharp;
using OpenCvSharp.Extensions;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

// 충돌 방지 별칭
using DPoint = System.Drawing.Point;
using DRect = System.Drawing.Rectangle;

namespace ImageCropTool
{
    public partial class MainForm : Form
    {
        /* =========================================================
         *  고정 설정값
         * ========================================================= */
        private const int DefaultCropSize = 512;
        private const int HitRadius = 8;

        /* =========================================================
         *  이미지 필드
         * ========================================================= */
        private Bitmap originalBitmap;
        private Mat originalMat;
        private Bitmap viewBitmap;

        /* =========================================================
         *  상태 관리
         * ========================================================= */
        private bool isImageLoading = false;
        private Timer loadingTimer;
        private float spinnerAngle = 0f;

        /* =========================================================
         *  클릭 상태 머신
         * ========================================================= */
        private enum ClickState { None, OnePoint, TwoPoints }
        private ClickState clickState = ClickState.None;

        /* =========================================================
         *  드래그 상태
         * ========================================================= */
        private bool isDragging = false;
        private bool draggingFirst = false;
        private bool draggingSecond = false;
        private bool invalidClickNotified = false;

        /* =========================================================
         *  좌표 (View / Original)
         * ========================================================= */
        private DPoint firstViewPt;
        private DPoint secondViewPt;
        private PointF firstOriginalPt;
        private PointF secondOriginalPt;

        /* =========================================================
         *  계산 결과
         * ========================================================= */
        private float lineLength = 0f;
        private int cropCount = 0;

        /* =========================================================
         *  Anchor 정책
         * ========================================================= */
        private enum CropAnchor
        {
            Center,
            TopLeft,
            TopRight,
            BottomLeft,
            BottomRight
        }
        private CropAnchor cropAnchor = CropAnchor.Center;

        /* =========================================================
         *  생성자
         * ========================================================= */
        public MainForm()
        {
            InitializeComponent();

            loadingTimer = new Timer { Interval = 50 };
            loadingTimer.Tick += (s, e) =>
            {
                spinnerAngle = (spinnerAngle + 20) % 360;
                pictureBoxImage.Invalidate();
            };

            pictureBoxImage.Paint += pictureBoxImage_Paint;
            pictureBoxImage.MouseDown += pictureBoxImage_MouseDown;
            pictureBoxImage.MouseMove += pictureBoxImage_MouseMove;
            pictureBoxImage.MouseUp += pictureBoxImage_MouseUp;

            numCropSize.Value = DefaultCropSize;
        }

        /* =========================================================
         *  초기화
         * ========================================================= */
        private void ClearPoints()
        {
            clickState = ClickState.None;
            firstViewPt = DPoint.Empty;
            secondViewPt = DPoint.Empty;
            firstOriginalPt = PointF.Empty;
            secondOriginalPt = PointF.Empty;
            lineLength = 0f;
            cropCount = 0;
            lblLineLength.Text = "Line Length: -";
            lblCropCount.Text = "Crop Count: -";
            pictureBoxImage.Invalidate();
        }

        private void btnReset_Click(object sender, EventArgs e) => ClearPoints();

        /* =========================================================
         *  이미지 로딩
         * ========================================================= */
        private async void btnLoadImage_Click(object sender, EventArgs e)
        {
            OpenFileDialog dlg = new OpenFileDialog { Filter = "Image Files|*.bmp;*.jpg;*.png" };
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
                Bitmap preview = null;
                await Task.Run(() =>
                {
                    using (Bitmap full = new Bitmap(dlg.FileName))
                    {
                        preview = ResizeToFit(full, pictureBoxImage.Width, pictureBoxImage.Height);
                    }
                });

                viewBitmap?.Dispose();
                viewBitmap = preview;
                pictureBoxImage.Image = viewBitmap;

                await Task.Run(() =>
                {
                    originalBitmap?.Dispose();
                    originalMat?.Dispose();
                    originalBitmap = new Bitmap(dlg.FileName);
                    originalMat = BitmapConverter.ToMat(originalBitmap);
                });

                ClearPoints();
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

        /* =========================================================
         *  마우스 이벤트
         * ========================================================= */
        private void pictureBoxImage_MouseDown(object sender, MouseEventArgs e)
        {
            if (isImageLoading || viewBitmap == null)
                return;

            DRect imageRect = GetImageViewRect();

            // 이미지 영역 외 클릭 차단
            if (e.Button == MouseButtons.Left && !imageRect.Contains(e.Location))
            {
                if (!invalidClickNotified)
                {
                    invalidClickNotified = true;
                    MessageBox.Show("이미지 영역을 클릭하세요.");
                }
                return;
            }
            invalidClickNotified = false;

            if (clickState != ClickState.None)
            {
                if (IsHit(e.Location, firstViewPt))
                {
                    isDragging = true;
                    draggingFirst = true;
                    return;
                }
                if (clickState == ClickState.TwoPoints && IsHit(e.Location, secondViewPt))
                {
                    isDragging = true;
                    draggingSecond = true;
                    return;
                }
            }

            if (clickState == ClickState.None)
            {
                firstViewPt = e.Location;
                firstOriginalPt = ViewToOriginal(firstViewPt);
                clickState = ClickState.OnePoint;
            }
            else if (clickState == ClickState.OnePoint)
            {
                secondViewPt = e.Location;
                secondOriginalPt = ViewToOriginal(secondViewPt);
                clickState = ClickState.TwoPoints;
                UpdateLineInfo();
            }
            else
            {
                ClearPoints();
            }

            pictureBoxImage.Invalidate();
        }

        private void pictureBoxImage_MouseMove(object sender, MouseEventArgs e)
        {
            if (!isDragging)
                return;

            if (draggingFirst)
            {
                firstViewPt = e.Location;
                firstOriginalPt = ViewToOriginal(firstViewPt);
            }
            else if (draggingSecond)
            {
                secondViewPt = e.Location;
                secondOriginalPt = ViewToOriginal(secondViewPt);
            }

            UpdateLineInfo();
            pictureBoxImage.Invalidate();
        }

        private void pictureBoxImage_MouseUp(object sender, MouseEventArgs e)
        {
            isDragging = false;
            draggingFirst = false;
            draggingSecond = false;
        }

        /* =========================================================
         *  Paint
         * ========================================================= */
        private void pictureBoxImage_Paint(object sender, PaintEventArgs e)
        {
            if (isImageLoading)
            {
                DrawLoadingSpinner(e.Graphics);
                return;
            }
            if (viewBitmap == null)
                return;

            using (Pen pen = new Pen(Color.Red, 2))
            {
                if (clickState != ClickState.None)
                    DrawPoint(e.Graphics, firstViewPt);

                if (clickState == ClickState.TwoPoints)
                {
                    DrawPoint(e.Graphics, secondViewPt);
                    e.Graphics.DrawLine(pen, firstViewPt, secondViewPt);
                }
            }

            DrawGuideBoxes(e.Graphics);
        }

        /* =========================================================
         *  가이드 박스
         * ========================================================= */
        private void DrawGuideBoxes(Graphics g)
        {
            var boxes = CalculateCropBoxesOriginal();
            if (boxes.Count == 0)
                return;

            DRect r = GetImageViewRect();
            float scale = (float)r.Width / originalBitmap.Width;  // 원본 -> view 스케일 계산

            using (Pen pen = new Pen(Color.Yellow, 2) { DashStyle = DashStyle.Dash })
            {
                foreach (var box in boxes)
                {
                    PointF center = new PointF(box.X + box.Width / 2f, box.Y + box.Height / 2f);
                    DPoint viewCenter = OriginalToView(center);
                    int half = (int)(box.Width * scale / 2f);
                    g.DrawRectangle(pen, viewCenter.X - half, viewCenter.Y - half, half * 2, half * 2);
                }
            }
        }

        /* =========================================================
         *  계산 로직
         * ========================================================= */
        private void UpdateLineInfo()
        {
            if (clickState != ClickState.TwoPoints)
                return;

            float dx = secondOriginalPt.X - firstOriginalPt.X;
            float dy = secondOriginalPt.Y - firstOriginalPt.Y;
            lineLength = (float)Math.Sqrt(dx * dx + dy * dy);

            int cropSize = (int)numCropSize.Value;
            cropCount = (int)Math.Floor((lineLength + cropSize / 2f) / cropSize) + 1;

            lblLineLength.Text = $"Line Length: {lineLength:F1}px";
            lblCropCount.Text = $"Crop Count: {cropCount}";
        }
        private void numCropSize_ValueChanged(object sender, EventArgs e)
        {
            if (clickState != ClickState.TwoPoints)
                return;

            UpdateLineInfo();
            pictureBoxImage.Invalidate();
        }

        private bool IsHit(DPoint p, DPoint target)
        {
            return Math.Abs(p.X - target.X) <= HitRadius && Math.Abs(p.Y - target.Y) <= HitRadius;
        }

        private void DrawPoint(Graphics g, DPoint pt)
        {
            g.FillEllipse(Brushes.Red, pt.X - 4, pt.Y - 4, 8, 8);
        }

        /* =========================================================
         *  Anchor & 박스 계산
         * ========================================================= */
        private PointF AnchorToBox(PointF anchor, float size)
        {
            switch (cropAnchor)
            {
                case CropAnchor.Center:
                    return new PointF(
                        anchor.X - size / 2f,
                        anchor.Y - size / 2f
                    );

                case CropAnchor.TopLeft:
                    return anchor;

                case CropAnchor.TopRight:
                    return new PointF(
                        anchor.X - size,
                        anchor.Y
                    );

                case CropAnchor.BottomLeft:
                    return new PointF(
                        anchor.X,
                        anchor.Y - size
                    );

                case CropAnchor.BottomRight:
                    return new PointF(
                        anchor.X - size,
                        anchor.Y - size
                    );

                default:
                    return anchor;
            }
        }


        private List<RectangleF> CalculateCropBoxesOriginal()   // 박스 계산 담당
        {
            List<RectangleF> boxes = new List<RectangleF>();
            if (clickState != ClickState.TwoPoints)
                return boxes;

            int cropSize = (int)numCropSize.Value;
            float dx = secondOriginalPt.X - firstOriginalPt.X;
            float dy = secondOriginalPt.Y - firstOriginalPt.Y;
            float length = (float)Math.Sqrt(dx * dx + dy * dy);
            if (length < 1f)
                return boxes;

            float ux = dx / length;
            float uy = dy / length;

            for (float dist = 0; dist <= length + cropSize / 2f; dist += cropSize)
            {
                PointF anchor = new PointF(
                    firstOriginalPt.X + ux * dist,
                    firstOriginalPt.Y + uy * dist);  // 선 방향으로 dist만큼 이동한 기준점
                PointF tl = AnchorToBox(anchor, cropSize);   // 설정한 기준점 계산
                boxes.Add(new RectangleF(tl.X, tl.Y, cropSize, cropSize));  // 박스 위치 및 사이즈
            }
            return boxes;
        }

        /* =========================================================
         *  좌표 변환
         * ========================================================= */
        private PointF ViewToOriginal(DPoint pt)
        {
            DRect r = GetImageViewRect();
            return new PointF(
                (float)(pt.X - r.X) / r.Width * originalBitmap.Width,
                (float)(pt.Y - r.Y) / r.Height * originalBitmap.Height);
        }

        private DPoint OriginalToView(PointF pt)
        {
            DRect r = GetImageViewRect();
            return new DPoint(
                r.X + (int)(pt.X / originalBitmap.Width * r.Width),
                r.Y + (int)(pt.Y / originalBitmap.Height * r.Height));
        }

        /* =========================================================
         *  크롭 저장
         * ========================================================= */
        private void btnCropSave_Click(object sender, EventArgs e) => CropAndSaveAll();

        private void CropAndSaveAll()
        {
            var boxes = CalculateCropBoxesOriginal();
            if (boxes.Count == 0)
                return;

            string folder = Path.Combine(Application.StartupPath, "Crops", DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            Directory.CreateDirectory(folder);

            int index = 1;
            foreach (var box in boxes)
            {
                int x = (int)Math.Max(0, Math.Min(box.X, originalMat.Width - box.Width));    // 유효한 좌표
                int y = (int)Math.Max(0, Math.Min(box.Y, originalMat.Height - box.Height));  // 경계 보호

                using (Mat cropped = new Mat(originalMat, new Rect(x, y, (int)box.Width, (int)box.Height)))
                {
                    Cv2.ImWrite(Path.Combine(folder, $"crop_{index++:D3}.png"), cropped);
                }
            }

            MessageBox.Show($"저장 완료: {index - 1}개");
        }

        /* =========================================================
         *  기타 유틸
         * ========================================================= */
        private Bitmap ResizeToFit(Bitmap src, int maxW, int maxH)
        {
            double scale = Math.Min((double)maxW / src.Width, (double)maxH / src.Height);
            Bitmap dst = new Bitmap((int)(src.Width * scale), (int)(src.Height * scale));

            using (Graphics g = Graphics.FromImage(dst))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(src, 0, 0, dst.Width, dst.Height);
            }
            return dst;
        }

        private DRect GetImageViewRect()
        {
            if (viewBitmap == null)
                return DRect.Empty;

            float imageRatio = (float)viewBitmap.Width / viewBitmap.Height;
            float boxRatio = (float)pictureBoxImage.Width / pictureBoxImage.Height;

            if (imageRatio > boxRatio)
            {
                int h = (int)(pictureBoxImage.Width / imageRatio);
                return new DRect(0, (pictureBoxImage.Height - h) / 2, pictureBoxImage.Width, h);
            }
            else
            {
                int w = (int)(pictureBoxImage.Height * imageRatio);
                return new DRect((pictureBoxImage.Width - w) / 2, 0, w, pictureBoxImage.Height);
            }
        }

        private void DrawLoadingSpinner(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int size = 50;
            int x = (pictureBoxImage.Width - size) / 2;
            int y = (pictureBoxImage.Height - size) / 2; 

            using (Pen bgPen = new Pen(Color.FromArgb(50, Color.Gray), 6))
            using (Pen fgPen = new Pen(Color.DeepSkyBlue, 6))
            {
                g.DrawEllipse(bgPen, x, y, size, size);
                g.DrawArc(fgPen, x, y, size, size, spinnerAngle, 100);
            }

            using (Font font = new Font("맑은 고딕", 10, FontStyle.Bold))
            {
                string msg = "Loading...";
                SizeF ts = g.MeasureString(msg, font);
                g.DrawString(msg, font, Brushes.DimGray, (pictureBoxImage.Width - ts.Width) / 2, y + size + 10);
            }
        }
    }
}
