using OpenCvSharp;
using OpenCvSharp.Extensions;
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

// System.Drawing과 OpenCV의 Point / Rectangle 충돌 방지용 별칭
using DPoint = System.Drawing.Point;
using DRect = System.Drawing.Rectangle;

namespace ImageCropTool
{
    public partial class MainForm : Form
    {
        /* =========================================================
         * 1. 고정 설정값 (상수)
         * ========================================================= */

        // 기본 크롭 사이즈 (px)
        private const int DefaultCropSize = 512;

        // 점 드래그 판정을 위한 히트 반경
        private const int HitRadius = 8;


        /* =========================================================
         * 2. 이미지 관련 필드
         * ========================================================= */

        // 실제 원본 이미지 (크롭 기준)
        private Bitmap originalBitmap;

        // OpenCV 처리용 Mat
        private Mat originalMat;

        // PictureBox에 표시되는 축소 이미지
        private Bitmap viewBitmap;

        // View <-> Original 좌표 변환 비율
        private float scaleX = 1f;
        private float scaleY = 1f;


        /* =========================================================
         * 3. 상태 관리 변수
         * ========================================================= */

        // 이미지 로딩 중 여부
        private bool isImageLoading = false;

        // 로딩 스피너용 타이머
        private Timer loadingTimer;

        // 스피너 회전 각도
        private float spinnerAngle = 0f;


        /* =========================================================
         * 4. 클릭 상태 관리 (상태 머신)
         * ========================================================= */

        private enum ClickState
        {
            None,       // 점 없음
            OnePoint,   // 첫 번째 점만 있음
            TwoPoints   // 두 점 모두 있음
        }

        private ClickState clickState = ClickState.None;


        /* =========================================================
         * 5. 드래그 관련 상태
         * ========================================================= */

        private bool isDragging = false;
        private bool draggingFirst = false;
        private bool draggingSecond = false;

        // 잘못된 클릭 안내 중복 방지
        private bool invalidClickNotified = false;


        /* =========================================================
         * 6. 좌표 정보
         * ========================================================= */

        // View 좌표 (PictureBox 기준)
        private DPoint firstViewPt;
        private DPoint secondViewPt;

        // Original 좌표 (실제 이미지 기준)
        private PointF firstOriginalPt;
        private PointF secondOriginalPt;


        /* =========================================================
         * 7. 계산 결과 정보
         * ========================================================= */

        private float lineLength = 0f;
        private int cropCount = 0;

        /* =========================================================
         * 8. 생성자
         * ========================================================= */
        public MainForm()
        {
            InitializeComponent();

            // 로딩 스피너 타이머 초기화
            loadingTimer = new Timer
            {
                Interval = 50
            };

            loadingTimer.Tick += (s, e) =>
            {
                spinnerAngle = (spinnerAngle + 20) % 360;
                pictureBoxImage.Invalidate();
            };

            // PictureBox 이벤트 연결
            pictureBoxImage.Paint += pictureBoxImage_Paint;
            pictureBoxImage.MouseDown += pictureBoxImage_MouseDown;
            pictureBoxImage.MouseMove += pictureBoxImage_MouseMove;
            pictureBoxImage.MouseUp += pictureBoxImage_MouseUp;

            // 기본 크롭 사이즈 설정
            numCropSize.Value = DefaultCropSize;
        }


        /* =========================================================
         * 9. 초기화 관련
         * ========================================================= */

        /// <summary>
        /// 점, 선, 계산 결과만 초기화
        /// (사용자가 설정한 cropSize는 유지)
        /// </summary>
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

        /// <summary>
        /// 완전 초기화 (이미지 로드 시, Reset 버튼)
        /// </summary>
        private void ResetAll()
        {
            ClearPoints();
            numCropSize.Value = DefaultCropSize;
        }

        private void btnReset_Click(object sender, EventArgs e)
        {
            ResetAll();
        }


        /* =========================================================
         * 10. 이미지 로딩
         * ========================================================= */

        private async void btnLoadImage_Click(object sender, EventArgs e)
        {
            OpenFileDialog dlg = new OpenFileDialog
            {
                Filter = "Image Files|*.bmp;*.jpg;*.png"
            };

            if (dlg.ShowDialog() != DialogResult.OK)
                return;

            // UI 비활성화 + 로딩 시작
            isImageLoading = true;
            loadingTimer.Start();

            pictureBoxImage.Enabled = false;
            btnCropSave.Enabled = false;
            btnLoadImage.Enabled = false;

            try
            {
                Bitmap preview = null;

                // View 이미지 생성 (비동기)
                await Task.Run(() =>
                {
                    using (Bitmap full = new Bitmap(dlg.FileName))
                    {
                        preview = ResizeToFit(
                            full,
                            pictureBoxImage.Width,
                            pictureBoxImage.Height
                        );
                    }
                });

                viewBitmap?.Dispose();
                viewBitmap = preview;
                pictureBoxImage.Image = viewBitmap;

                // 원본 이미지 & Mat 로드
                await Task.Run(() =>
                {
                    originalBitmap?.Dispose();
                    originalMat?.Dispose();

                    originalBitmap = new Bitmap(dlg.FileName);
                    originalMat = BitmapConverter.ToMat(originalBitmap);
                });

                // 좌표 변환 비율 계산
                scaleX = (float)originalBitmap.Width / viewBitmap.Width;
                scaleY = (float)originalBitmap.Height / viewBitmap.Height;

                ResetAll();
            }
            catch (Exception ex)
            {
                MessageBox.Show("이미지 로드 실패: " + ex.Message);
            }
            finally
            {
                // UI 복구
                isImageLoading = false;
                loadingTimer.Stop();

                pictureBoxImage.Enabled = true;
                btnCropSave.Enabled = true;
                btnLoadImage.Enabled = true;

                pictureBoxImage.Invalidate();
            }
        }


        /* =========================================================
         * 11. 마우스 이벤트
         * ========================================================= */

        private void pictureBoxImage_MouseDown(object sender, MouseEventArgs e)
        {
            if (isImageLoading || viewBitmap == null)
                return;

            DRect imageRect = GetImageViewRect();

            // 이미지 영역 외 클릭 방지
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

            // 기존 점 드래그 판정
            if (clickState != ClickState.None)
            {
                if (IsHit(e.Location, firstViewPt))
                {
                    isDragging = true;
                    draggingFirst = true;
                    return;
                }

                if (clickState == ClickState.TwoPoints &&
                    IsHit(e.Location, secondViewPt))
                {
                    isDragging = true;
                    draggingSecond = true;
                    return;
                }
            }

            // 상태별 클릭 처리
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
                // 두 점이 이미 있으면 점만 초기화
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
         * 12. 그리기 (Paint)
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

        /// <summary>
        /// 두 점 사이의 직선을 따라 cropSize 간격으로
        /// 크롭 가이드 박스를 화면에 그린다
        /// </summary>
        private void DrawGuideBoxes(Graphics g)
        {
            // 두 점이 모두 있어야만 가이드 표시
            if (clickState != ClickState.TwoPoints)
                return;

            int cropSize = (int)numCropSize.Value;

            if (cropSize <= 0)
                return;

            // 원본 좌표 기준 방향 벡터 계산
            float dx = secondOriginalPt.X - firstOriginalPt.X;
            float dy = secondOriginalPt.Y - firstOriginalPt.Y;

            float length = (float)Math.Sqrt(dx * dx + dy * dy);

            if (length < 1f)
                return;

            // 단위 방향 벡터
            float ux = dx / length;
            float uy = dy / length;

            using (Pen pen = new Pen(Color.Yellow, 2))
            {
                pen.DashStyle = DashStyle.Dash;

                // 직선을 따라 cropSize 간격으로 반복
                for (float dist = 0; dist <= length; dist += cropSize)
                {
                    // 원본 좌표 기준 중심점
                    PointF originalCenter = new PointF(
                        firstOriginalPt.X + ux * dist,
                        firstOriginalPt.Y + uy * dist
                    );

                    // View 좌표로 변환
                    DPoint viewCenter = OriginalToView(originalCenter);

                    // View 기준 half size 계산
                    int halfSize = (int)((cropSize / 2f) / scaleX);

                    g.DrawRectangle(
                        pen,
                        viewCenter.X - halfSize,
                        viewCenter.Y - halfSize,
                        halfSize * 2,
                        halfSize * 2
                    );
                }
            }
        }

        /* =========================================================
         * 13. 유틸리티 / 계산
         * ========================================================= */

        private void UpdateLineInfo()
        {
            if (clickState != ClickState.TwoPoints)
                return;

            float dx = secondOriginalPt.X - firstOriginalPt.X;
            float dy = secondOriginalPt.Y - firstOriginalPt.Y;

            lineLength = (float)Math.Sqrt(dx * dx + dy * dy);

            cropCount = (int)Math.Ceiling(
                lineLength / (int)numCropSize.Value
            );

            lblLineLength.Text = $"Line Length: {lineLength:F1}px";
            lblCropCount.Text = $"Crop Count: {cropCount}";
        }

        private void numCropSize_ValueChanged(object sender, EventArgs e)
        {
            UpdateLineInfo();
            pictureBoxImage.Invalidate();
        }

        private bool IsHit(DPoint p, DPoint target)
        {
            return Math.Abs(p.X - target.X) <= HitRadius &&
                   Math.Abs(p.Y - target.Y) <= HitRadius;
        }

        private void DrawPoint(Graphics g, DPoint pt)
        {
            g.FillEllipse(
                Brushes.Red,
                pt.X - 4,
                pt.Y - 4,
                8,
                8
            );
        }

        /* =========================================================
         * 14. 좌표 변환
         * ========================================================= */
        private PointF ViewToOriginal(DPoint pt)
        {
            DRect r = GetImageViewRect();

            return new PointF(
                (float)(pt.X - r.X) / r.Width * originalBitmap.Width,
                (float)(pt.Y - r.Y) / r.Height * originalBitmap.Height
            );
        }

        private DPoint OriginalToView(PointF pt)
        {
            DRect r = GetImageViewRect();

            return new DPoint(
                r.X + (int)(pt.X / originalBitmap.Width * r.Width),
                r.Y + (int)(pt.Y / originalBitmap.Height * r.Height)
            );
        }


        /* =========================================================
         * 15. 크롭 및 저장
         * ========================================================= */
        private void btnCropSave_Click(object sender, EventArgs e)
        {
            CropAndSaveAll();
        }

        private void CropAndSaveAll()
        {
            if (clickState != ClickState.TwoPoints)
                return;

            int cropSize = (int)numCropSize.Value;

            float dx = secondOriginalPt.X - firstOriginalPt.X;
            float dy = secondOriginalPt.Y - firstOriginalPt.Y;

            float length = (float)Math.Sqrt(dx * dx + dy * dy);

            float ux = dx / length;
            float uy = dy / length;

            string folder = Path.Combine(
                Application.StartupPath,
                "Crops",
                DateTime.Now.ToString("yyyyMMdd_HHmmss")
            );

            Directory.CreateDirectory(folder);

            int total = (int)Math.Ceiling(length / cropSize);

            for (int i = 0; i < total; i++)
            {
                float dist = Math.Min(i * cropSize, length);

                float cx = firstOriginalPt.X + ux * dist;
                float cy = firstOriginalPt.Y + uy * dist;

                int x = Math.Max(0, (int)(cx - cropSize / 2f));
                int y = Math.Max(0, (int)(cy - cropSize / 2f));

                int w = Math.Min(cropSize, originalMat.Width - x);
                int h = Math.Min(cropSize, originalMat.Height - y);

                if (w <= 0 || h <= 0)
                    continue;

                using (Mat cropped = new Mat(
                    originalMat,
                    new Rect(x, y, w, h)))
                {
                    Cv2.ImWrite(
                        Path.Combine(folder, $"crop_{i + 1:D3}.png"),
                        cropped
                    );
                }
            }

            MessageBox.Show($"저장 완료: {total}개");
        }


        /* =========================================================
         * 16. 기타 유틸
         * ========================================================= */
        private Bitmap ResizeToFit(Bitmap src, int maxW, int maxH)
        {
            double scale = Math.Min(
                (double)maxW / src.Width,
                (double)maxH / src.Height
            );

            Bitmap dst = new Bitmap(
                (int)(src.Width * scale),
                (int)(src.Height * scale)
            );

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

                return new DRect(
                    0,
                    (pictureBoxImage.Height - h) / 2,
                    pictureBoxImage.Width,
                    h
                );
            }
            else
            {
                int w = (int)(pictureBoxImage.Height * imageRatio);

                return new DRect(
                    (pictureBoxImage.Width - w) / 2,
                    0,
                    w,
                    pictureBoxImage.Height
                );
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

            string msg = "Loading...";

            using (Font font = new Font("맑은 고딕", 10, FontStyle.Bold))
            {
                SizeF textSize = g.MeasureString(msg, font);

                g.DrawString(
                    msg,
                    font,
                    Brushes.DimGray,
                    (pictureBoxImage.Width - textSize.Width) / 2,
                    y + size + 10
                );
            }
        }
    }
}
