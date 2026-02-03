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

namespace ImageCropTool
{
    public partial class MainForm : Form
    {
        // 초기화
        private float lineLength = 0f;
        private int cropCount = 0;
        private const int DefaultCropSize = 512;

        //  이미지 관련
        private Bitmap viewBitmap;
        private Bitmap originalBitmap;
        private Mat originalMat;

        private bool isImageLoading = false;
        private Timer loadingTimer;
        private float spinnerAngle = 0f;

        // 드래그 관련 상태    
        private bool isDraggingPoint = false;
        private bool draggingFirstPoint = false;
        private bool draggingSecondPoint = false;
        private const int HitRadius = 8;

        // 클릭 상태 머신
        private enum ClickState { None, OnePoint, TwoPoints }
        private ClickState clickState = ClickState.None;

        // 좌표
        private PointF firstOriginalPt;
        private PointF secondOriginalPt;

        // 크롭 박스 기준점
        private enum CropAnchor
        {
            Center,
            TopLeft,
            TopRight,
            BottomLeft,
            BottomRight
        }
        private CropAnchor cropAnchor = CropAnchor.Center;

        // View Transform (Zoom & Pan)
        private float viewScale = 1.0f;                 // 줌 배율
        private PointF viewOffset = new PointF(0, 0);   // 팬 이동량

        private const float ZoomStep = 1.1f;
        private const float MinZoom = 0.2f;
        private const float MaxZoom = 5.0f;

        private bool isPanning = false;
        private DPoint lastMousePt;

        // =============================
        //    생성자
        // =============================
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
            pictureBoxImage.Paint += pictureBoxImage_Paint;
            pictureBoxImage.MouseDown += pictureBoxImage_MouseDown;
            pictureBoxImage.MouseMove += pictureBoxImage_MouseMove;
            pictureBoxImage.MouseUp += pictureBoxImage_MouseUp;
            pictureBoxImage.MouseWheel += pictureBoxImage_MouseWheel;
            pictureBoxImage.MouseEnter += (s, e) => pictureBoxImage.Focus();

            numCropSize.Value = DefaultCropSize;
        }

        // =====================================================
        // 초기화 함수들
        // =====================================================
        private void ResetView()   // 이미지 출력 초기화
        {
            viewScale = 1.0f;   // 원래 배율

            if (viewBitmap != null)
            {
                viewOffset = new PointF(    // 이미지 정중앙에 오게
                    (pictureBoxImage.Width - viewBitmap.Width) / 2f,
                    (pictureBoxImage.Height - viewBitmap.Height) / 2f  
                );
            }
            else
            {
                viewOffset = new PointF(0, 0);  // 이미지 없으면
            }
        }

        private void btnReset_Click(object sender, EventArgs e) => ResetAll();
        private void ResetAll()
        {
            ClearPoints();
            ResetView();
            numCropSize.Value = DefaultCropSize;
        }

        private void ClearPoints()
        {
            clickState = ClickState.None;
            firstOriginalPt = secondOriginalPt = PointF.Empty;
            lineLength = 0f;
            cropCount = 0;
            lblLineLength.Text = "Line Length: -";
            lblCropCount.Text = "Crop Count: -";
            pictureBoxImage.Invalidate();
        }

        private void numCropSize_ValueChanged(object sender, EventArgs e)
        {
            if (clickState != ClickState.TwoPoints)
                return;

            UpdateLineInfo();
            pictureBoxImage.Invalidate();
        }

        // =====================================================
        // 이미지 로딩
        // =====================================================
        private async void btnLoadImage_Click(object sender, EventArgs e)
        {
            OpenFileDialog dlg = new OpenFileDialog { Filter = "Image Files|*.bmp;*.jpg;*.png" };
            if (dlg.ShowDialog() != DialogResult.OK)
                return;

            isImageLoading = true;
            loadingTimer.Start();

            pictureBoxImage.Enabled = false;   // pictureBox 컨트롤 비활성화(클릭/드래그 못함)
            btnCropSave.Enabled = false;       // 저장 버튼 비활성화
            btnLoadImage.Enabled = false;      // 이미지 불러오기 버튼 비활성화
            btnReset.Enabled = false;

            try
            {
                await Task.Run(() =>
                {
                    using (Bitmap full = new Bitmap(dlg.FileName))
                    {
                        viewBitmap = ResizeToFit(full, pictureBoxImage.Width, pictureBoxImage.Height);
                    }

                    originalBitmap?.Dispose();
                    originalMat?.Dispose();

                    originalBitmap = new Bitmap(dlg.FileName);
                    originalMat = BitmapConverter.ToMat(originalBitmap);
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

        // =====================================================
        // 마우스 이벤트
        // =====================================================
        private void pictureBoxImage_MouseDown(object sender, MouseEventArgs e)
        {
            if (viewBitmap == null)
                return;

            //  이미지 영역 밖 클릭 차단
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
                case MouseButtons.Right:  //  우클릭 드래그 이미지 이동

                    isPanning = true;
                    lastMousePt = e.Location;
                    return;

                case MouseButtons.Left:

                    // 기존 점 히트 테스트 (View 좌표 기준)
                    if (clickState != ClickState.None) 
                    {
                        DPoint firstView = OriginalToScreen(firstOriginalPt);

                        if (IsHit(e.Location, firstView))
                        {
                            isDraggingPoint = true;
                            draggingFirstPoint = true;
                            return;
                        }

                        if (clickState == ClickState.TwoPoints)
                        {
                            DPoint secondView = OriginalToScreen(secondOriginalPt);

                            if (IsHit(e.Location, secondView))
                            { 
                                isDraggingPoint = true;
                                draggingSecondPoint = true;
                                return;
                            }
                        }
                    }

                    // 점 새로 찍기
                    PointF pt = ViewToOriginal(e.Location);

                    if (clickState == ClickState.None)
                    {
                        firstOriginalPt = pt;
                        clickState = ClickState.OnePoint;
                    }
                    else if (clickState == ClickState.OnePoint)
                    {
                        secondOriginalPt = pt;
                        clickState = ClickState.TwoPoints;
                        UpdateLineInfo();
                    }
                    else
                    {
                        ClearPoints();
                    }
                    pictureBoxImage.Invalidate();
                    break;
            }
        }

        private void pictureBoxImage_MouseMove(object sender, MouseEventArgs e)
        {
            // 이미지 이동(패닝)
            if (isPanning)
            {
                viewOffset.X += e.X - lastMousePt.X;
                viewOffset.Y += e.Y - lastMousePt.Y;    // 이동 거리 계산
                lastMousePt = e.Location;               // 기준점 갱신
                pictureBoxImage.Invalidate();
                return;
            }

            // 점 위치 이동(드래그)
            if (isDraggingPoint)
            {
                PointF originalPt = ViewToOriginal(e.Location);

                if (draggingFirstPoint)
                    firstOriginalPt = originalPt;

                else if (draggingSecondPoint)
                    secondOriginalPt = originalPt;

                UpdateLineInfo();
                pictureBoxImage.Invalidate();
            }
        }


        private void pictureBoxImage_MouseUp(object sender, MouseEventArgs e)
        {
            isPanning = false;
            isDraggingPoint = false;
            draggingFirstPoint = false;
            draggingSecondPoint = false;
        }
        private void pictureBoxImage_MouseWheel(object sender, MouseEventArgs e)
        {
            float oldScale = viewScale;   // 확대 전 스케일 저장

            viewScale = e.Delta > 0 ? viewScale * ZoomStep : viewScale / ZoomStep;   // 휠 한칸당 10% 확대/축소
            viewScale = Math.Max(MinZoom, Math.Min(MaxZoom, viewScale));  // 줌 한계 제한

            viewOffset.X = e.X - (e.X - viewOffset.X) * (viewScale / oldScale);   // 마우스 위치 - 새 거리
            viewOffset.Y = e.Y - (e.Y - viewOffset.Y) * (viewScale / oldScale);

            pictureBoxImage.Invalidate();
        }

        // =====================================================
        // Paint(점,선,가이드박스)
        // =====================================================
        private void pictureBoxImage_Paint(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Color.Black);   // 이전 화면 초기화
            g.SmoothingMode = SmoothingMode.AntiAlias;

            if (isImageLoading)  // 로딩중
            {
                DrawLoadingSpinner(g);
                return;
            }

            if (viewBitmap == null)
                return;

            g.TranslateTransform(viewOffset.X, viewOffset.Y);   // 좌표계 이동(중앙정렬, 드래그 이동)
            g.ScaleTransform(viewScale, viewScale);             // 좌표계 스케일 변경(확대/축소 적용)

            g.DrawImage(viewBitmap, 0, 0);                      // 이미지 그리기

            DrawPointsAndLine(g);                               // 점, 선 그리기
            DrawGuideBoxes(g);                                  // 가이드 박스 그리기
                
            g.ResetTransform();                                 // 좌표계 원복
        }

        private void DrawPointsAndLine(Graphics g)   // 점&선 그리기
        {
            using (Pen pen = new Pen(Color.Red, 2 / viewScale))  // 확대/축소 해도 같은 두깨로 역보정
            {
                if (clickState != ClickState.None)
                    DrawPoint(g, firstOriginalPt);     // 첫 점 그림

                if (clickState == ClickState.TwoPoints)
                {
                    DrawPoint(g, secondOriginalPt);   // 두번째 점 그림

                    PointF p1 = OriginalToView(firstOriginalPt);
                    PointF p2 = OriginalToView(secondOriginalPt);
                    g.DrawLine(pen, p1, p2);      // 선 그림
                }
            }
        }

        private void DrawPoint(Graphics g, PointF originalPt)
        {
            PointF pt = OriginalToView(originalPt);   // 화면 좌표로
            float r = 4 / viewScale;   // 반지름 보정(확대/축소 해도 똑같게 역보정)
            g.FillEllipse(Brushes.Red, pt.X - r, pt.Y - r, r * 2, r * 2);   // 좌상단 기준이기 때문에 중심으로 보정
        }

        private void DrawGuideBoxes(Graphics g)
        {
            var boxes = CalculateCropBoxesOriginal();
            using (Pen pen = new Pen(Color.Yellow, 2 / viewScale) { DashStyle = DashStyle.Dash })
            {
                foreach (var box in boxes)
                {
                    PointF tl = OriginalToView(new PointF(box.X, box.Y));  // 크롭박스 기준점 view 좌표로 변환
                    float scaleX = (float)viewBitmap.Width / originalBitmap.Width;    // 가로 크기 비율 계산
                    float scaleY = (float)viewBitmap.Height / originalBitmap.Height;  // 세로 비율

                    g.DrawRectangle(
                        pen,
                        tl.X,
                        tl.Y,
                        box.Width * scaleX,
                        box.Height * scaleY
                    );
                }
            }
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



        // =====================================================
        // Logic
        // =====================================================
        private void UpdateLineInfo()
        {
            float dx = secondOriginalPt.X - firstOriginalPt.X;
            float dy = secondOriginalPt.Y - firstOriginalPt.Y;
            lineLength = (float)Math.Sqrt(dx * dx + dy * dy);

            int cropSize = (int)numCropSize.Value;
            cropCount = (int)Math.Floor((lineLength + cropSize / 2f) / cropSize) + 1;

            lblLineLength.Text = $"Line Length: {lineLength:F1}px";
            lblCropCount.Text = $"Crop Count: {cropCount}";
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


        private List<RectangleF> CalculateCropBoxesOriginal()
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
                // 선을 따라 이동한 기준점 (Anchor)
                PointF anchor = new PointF(
                    firstOriginalPt.X + ux * dist,
                    firstOriginalPt.Y + uy * dist);

                // Anchor 정책 적용 (여기가 핵심)
                PointF tl = AnchorToBox(anchor, cropSize);

                boxes.Add(new RectangleF(tl.X, tl.Y, cropSize, cropSize));
            }

            return boxes;
        }


        // =====================================================
        // Utils
        // =====================================================
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

        private PointF ViewToOriginal(DPoint viewPt)
        {
            float vx = (viewPt.X - viewOffset.X) / viewScale;
            float vy = (viewPt.Y - viewOffset.Y) / viewScale;

            float scaleX = (float)originalBitmap.Width / viewBitmap.Width;
            float scaleY = (float)originalBitmap.Height / viewBitmap.Height;

            return new PointF(vx * scaleX, vy * scaleY);
        }

        private PointF OriginalToView(PointF originalPt)
        {
            float scaleX = (float)viewBitmap.Width / originalBitmap.Width;
            float scaleY = (float)viewBitmap.Height / originalBitmap.Height;

            return new PointF(
                originalPt.X * scaleX,
                originalPt.Y * scaleY
            );
        }
        private bool IsHit(DPoint mousePt, DPoint targetPt)
        {
            return Math.Abs(mousePt.X - targetPt.X) <= HitRadius &&
                   Math.Abs(mousePt.Y - targetPt.Y) <= HitRadius;
        }
        private DPoint OriginalToScreen(PointF originalPt)
        {
            // original → viewBitmap
            float sx = (float)viewBitmap.Width / originalBitmap.Width;
            float sy = (float)viewBitmap.Height / originalBitmap.Height;

            float vx = originalPt.X * sx;
            float vy = originalPt.Y * sy;

            // viewBitmap → screen (zoom + pan)
            float screenX = vx * viewScale + viewOffset.X;
            float screenY = vy * viewScale + viewOffset.Y;

            return new DPoint(
                (int)Math.Round(screenX),
                (int)Math.Round(screenY)
            );
        }
        private bool IsInsideImageScreen(DPoint screenPt)   // 이미지 영역 판별
        {
            if (viewBitmap == null)
                return false;

            RectangleF rect = new RectangleF(
                viewOffset.X,
                viewOffset.Y,
                viewBitmap.Width * viewScale,
                viewBitmap.Height * viewScale
            );

            return rect.Contains(screenPt);
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
