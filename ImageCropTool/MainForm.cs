using OpenCvSharp;
using OpenCvSharp.Extensions;
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

using DPoint = System.Drawing.Point;
using DRect = System.Drawing.Rectangle;

namespace ImageCropTool
{
    public partial class MainForm : Form
    {
        // 고정 설정값
        private const int DefaultCropSize = 512;
        private const int HitRadius = 8;

        // 이미지 데이터
        private Bitmap originalBitmap;
        private Mat originalMat;
        private Bitmap viewBitmap;

        private float scaleX = 1f;
        private float scaleY = 1f;

        // 상태 변수
        private bool isImageLoading = false;
        private Timer loadingTimer;
        private float spinnerAngle = 0;

        private enum ClickState { None, OnePoint, TwoPoints }
        private ClickState clickState = ClickState.None;

        private bool isDragging = false;
        private bool draggingFirst = false;
        private bool draggingSecond = false;
        private bool invalidClickNotified = false;

        private DPoint firstViewPt;
        private DPoint secondViewPt;
        private PointF firstOriginalPt;
        private PointF secondOriginalPt;

        private float lineLength = 0f;
        private int cropCount = 0;

        public MainForm()
        {
            InitializeComponent();

            // 타이머 초기화
            loadingTimer = new Timer { Interval = 50 };
            loadingTimer.Tick += (s, e) => {
                spinnerAngle = (spinnerAngle + 20) % 360;
                pictureBoxImage.Invalidate();
            };

            // 이벤트 연결
            pictureBoxImage.Paint += pictureBoxImage_Paint;
            pictureBoxImage.MouseDown += pictureBoxImage_MouseDown;
            pictureBoxImage.MouseMove += pictureBoxImage_MouseMove;
            pictureBoxImage.MouseUp += pictureBoxImage_MouseUp;

            // 시작 시 기본값 설정
            numCropSize.Value = DefaultCropSize;
        }

        /* =========================================================
         * 초기화 관련 함수 
         * ========================================================= */

        // 1. 점과 계산된 정보만 지우기 (사용자 설정 숫자는 유지)
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

        // 2. 모든 것을 공장 초기화 (이미지 로드, 초기화 버튼 클릭 시)
        private void ResetAll()
        {
            ClearPoints();
            numCropSize.Value = DefaultCropSize; // 숫자를 다시 512로!
        }

        private void btnReset_Click(object sender, EventArgs e)
        {
            ResetAll();
        }

        /* =========================================================
         * 이미지 로딩
         * ========================================================= */
        private async void btnLoadImage_Click(object sender, EventArgs e)
        {
            OpenFileDialog dlg = new OpenFileDialog { Filter = "Image Files|*.bmp;*.jpg;*.png" };
            if (dlg.ShowDialog() != DialogResult.OK) return;

            isImageLoading = true;
            loadingTimer.Start();
            pictureBoxImage.Enabled = false;
            btnCropSave.Enabled = false;
            btnLoadImage.Enabled = false;

            try
            {
                Bitmap preview = null;
                await Task.Run(() => {
                    using (Bitmap full = new Bitmap(dlg.FileName))
                        preview = ResizeToFit(full, pictureBoxImage.Width, pictureBoxImage.Height);
                });

                viewBitmap?.Dispose();
                viewBitmap = preview;
                pictureBoxImage.Image = viewBitmap;

                await Task.Run(() => {
                    originalBitmap?.Dispose();
                    originalMat?.Dispose();
                    originalBitmap = new Bitmap(dlg.FileName);
                    originalMat = BitmapConverter.ToMat(originalBitmap);
                });

                scaleX = (float)originalBitmap.Width / viewBitmap.Width;
                scaleY = (float)originalBitmap.Height / viewBitmap.Height;

                ResetAll(); // 이미지 로드 시에는 설정값까지 초기화
            }
            catch (Exception ex) { MessageBox.Show("로드 실패: " + ex.Message); }
            finally
            {
                isImageLoading = false;
                loadingTimer.Stop();
                pictureBoxImage.Enabled = true;
                btnCropSave.Enabled = true;
                btnLoadImage.Enabled = true;
                pictureBoxImage.Invalidate();
            }
        }

        /* =========================================================
         * 마우스 이벤트 (수정됨)
         * ========================================================= */
        private void pictureBoxImage_MouseDown(object sender, MouseEventArgs e)
        {
            if (isImageLoading || viewBitmap == null) return;
            DRect imgRect = GetImageViewRect();

            if (e.Button == MouseButtons.Left && !imgRect.Contains(e.Location))
            {
                if (!invalidClickNotified)
                {
                    invalidClickNotified = true;
                    MessageBox.Show("이미지 영역을 클릭하세요.");
                }
                return;
            }
            invalidClickNotified = false;

            // 드래그 체크
            if (clickState != ClickState.None)
            {
                if (IsHit(e.Location, firstViewPt)) { isDragging = true; draggingFirst = true; return; }
                if (clickState == ClickState.TwoPoints && IsHit(e.Location, secondViewPt)) { isDragging = true; draggingSecond = true; return; }
            }

            // 상태별 클릭 동작
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
                // 이미 두 점이 찍혔는데 또 클릭하면 '점만' 지움 (숫자는 유지!)
                ClearPoints();
            }
            pictureBoxImage.Invalidate();
        }

        /* =========================================================
         * 화면 그리기 (스피너 포함)
         * ========================================================= */
        private void pictureBoxImage_Paint(object sender, PaintEventArgs e)
        {
            if (isImageLoading) { DrawLoadingSpinner(e.Graphics); return; }
            if (viewBitmap == null) return;

            using (Pen pen = new Pen(Color.Red, 2))
            {
                if (clickState != ClickState.None) DrawPoint(e.Graphics, firstViewPt);
                if (clickState == ClickState.TwoPoints)
                {
                    DrawPoint(e.Graphics, secondViewPt);
                    e.Graphics.DrawLine(pen, firstViewPt, secondViewPt);
                }
            }
            DrawGuideBoxes(e.Graphics);
        }

        // 로딩 스피너 그리기 함수
        private void DrawLoadingSpinner(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias; // 부드럽게 그리기

            int size = 50; // 스피너 크기
            int x = (pictureBoxImage.Width - size) / 2;
            int y = (pictureBoxImage.Height - size) / 2;

            using (Pen spinnerPen = new Pen(Color.DeepSkyBlue, 6))
            {
                // 전체 연한 원 그리기 (배경)
                using (Pen bgPen = new Pen(Color.FromArgb(50, Color.Gray), 6))
                {
                    g.DrawEllipse(bgPen, x, y, size, size);
                }

                // 회전하는 호(Arc) 그리기
                // spinnerAngle에 따라 시작 위치가 변함
                g.DrawArc(spinnerPen, x, y, size, size, spinnerAngle, 100);
            }

            // 안내 문구
            string msg = "Loading...";
            Font font = new Font("맑은 고딕", 10, FontStyle.Bold);
            SizeF fontSize = g.MeasureString(msg, font);
            g.DrawString(msg, font, Brushes.DimGray,
                (pictureBoxImage.Width - fontSize.Width) / 2,
                y + size + 10);
        }

        /* =========================================================
         * 유틸리티 (좌표 변환 및 가이드)
         * ========================================================= */
        private void DrawGuideBoxes(Graphics g)
        {
            if (clickState != ClickState.TwoPoints) return;
            int cropSize = (int)numCropSize.Value;
            if (cropSize <= 0) return;

            float dx = secondOriginalPt.X - firstOriginalPt.X;
            float dy = secondOriginalPt.Y - firstOriginalPt.Y;
            float length = (float)Math.Sqrt(dx * dx + dy * dy);
            if (length < 1) return;

            float ux = dx / length, uy = dy / length;
            using (Pen pen = new Pen(Color.Yellow, 2) { DashStyle = DashStyle.Dash })
            {
                for (float dist = 0; dist <= length; dist += cropSize)
                {
                    DPoint center = OriginalToView(new PointF(firstOriginalPt.X + ux * dist, firstOriginalPt.Y + uy * dist));
                    int half = (int)((cropSize / 2f) / scaleX);
                    g.DrawRectangle(pen, center.X - half, center.Y - half, half * 2, half * 2);
                }
            }
        }

        private void UpdateLineInfo()
        {
            if (clickState != ClickState.TwoPoints) return;
            float dx = secondOriginalPt.X - firstOriginalPt.X;
            float dy = secondOriginalPt.Y - firstOriginalPt.Y;
            lineLength = (float)Math.Sqrt(dx * dx + dy * dy);
            cropCount = (int)Math.Ceiling(lineLength / (int)numCropSize.Value);
            lblLineLength.Text = $"Line Length: {lineLength:F1}px";
            lblCropCount.Text = $"Crop Count: {cropCount}";
        }

        private void numCropSize_ValueChanged(object sender, EventArgs e) { UpdateLineInfo(); pictureBoxImage.Invalidate(); }
        private PointF ViewToOriginal(DPoint pt)
        {
            DRect r = GetImageViewRect();
            return new PointF((float)(pt.X - r.X) / r.Width * originalBitmap.Width, (float)(pt.Y - r.Y) / r.Height * originalBitmap.Height);
        }
        private DPoint OriginalToView(PointF pt)
        {
            DRect r = GetImageViewRect();
            return new DPoint(r.X + (int)(pt.X / originalBitmap.Width * r.Width), r.Y + (int)(pt.Y / originalBitmap.Height * r.Height));
        }
        private bool IsHit(DPoint p, DPoint target) => Math.Abs(p.X - target.X) <= HitRadius && Math.Abs(p.Y - target.Y) <= HitRadius;
        private void DrawPoint(Graphics g, DPoint pt) => g.FillEllipse(Brushes.Red, pt.X - 4, pt.Y - 4, 8, 8);
        private void pictureBoxImage_MouseMove(object sender, MouseEventArgs e)
        {
            if (!isDragging) return;

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
        private void pictureBoxImage_MouseUp(object sender, MouseEventArgs e) => isDragging = draggingFirst = draggingSecond = false;

        private Bitmap ResizeToFit(Bitmap src, int maxW, int maxH)
        {
            double s = Math.Min((double)maxW / src.Width, (double)maxH / src.Height);
            Bitmap dst = new Bitmap((int)(src.Width * s), (int)(src.Height * s));
            using (Graphics g = Graphics.FromImage(dst))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(src, 0, 0, dst.Width, dst.Height);
            }
            return dst;
        }

        private void btnCropSave_Click(object sender, EventArgs e) => CropAndSaveAll();

        private void CropAndSaveAll()
        {
            if (clickState != ClickState.TwoPoints) return;
            int cropSize = (int)numCropSize.Value;
            float dx = secondOriginalPt.X - firstOriginalPt.X, dy = secondOriginalPt.Y - firstOriginalPt.Y;
            float len = (float)Math.Sqrt(dx * dx + dy * dy);
            float ux = dx / len, uy = dy / len;
            string folder = Path.Combine(Application.StartupPath, "Crops", DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            Directory.CreateDirectory(folder);

            int total = (int)Math.Ceiling(len / cropSize);
            for (int i = 0; i < total; i++)
            {
                float dist = Math.Min(i * cropSize, len);
                float cx = firstOriginalPt.X + ux * dist, cy = firstOriginalPt.Y + uy * dist;
                int x = Math.Max(0, (int)Math.Round(cx - cropSize / 2f)), y = Math.Max(0, (int)Math.Round(cy - cropSize / 2f));
                int w = Math.Min(cropSize, originalMat.Width - x), h = Math.Min(cropSize, originalMat.Height - y);
                if (w <= 0 || h <= 0) continue;
                using (Mat cropped = new Mat(originalMat, new Rect(x, y, w, h)))
                    Cv2.ImWrite(Path.Combine(folder, $"crop_{i + 1:D3}.png"), cropped);
            }
            MessageBox.Show($"저장 완료: {total}개");
        }

        private DRect GetImageViewRect()
        {
            if (viewBitmap == null) return DRect.Empty;
            float iR = (float)viewBitmap.Width / viewBitmap.Height, bR = (float)pictureBoxImage.Width / pictureBoxImage.Height;
            return iR > bR ? new DRect(0, (pictureBoxImage.Height - (int)(pictureBoxImage.Width / iR)) / 2, pictureBoxImage.Width, (int)(pictureBoxImage.Width / iR))
                           : new DRect((pictureBoxImage.Width - (int)(pictureBoxImage.Height * iR)) / 2, 0, (int)(pictureBoxImage.Height * iR), pictureBoxImage.Height);
        }
    }
}