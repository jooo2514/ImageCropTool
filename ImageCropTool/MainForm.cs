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

        private bool showMousePos = false;
        private PointF mouseOriginalPt;   // 표시할 이미지 좌표
        private DPoint mouseScreenPt;     // 텍스트를 그릴 화면 위치


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
        private PointF viewOffset = new PointF(0, 0);   // 팬 이동량, 이미지 시작 위치

        private const float ZoomStep = 1.1f;    // 휠 한칸에 10%씩 변화
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
            pictureBoxImage.Paint += PictureBoxImage_Paint;
            pictureBoxImage.MouseDown += PictureBoxImage_MouseDown;
            pictureBoxImage.MouseMove += PictureBoxImage_MouseMove;
            pictureBoxImage.MouseUp += PictureBoxImage_MouseUp;
            pictureBoxImage.MouseWheel += PictureBoxImage_MouseWheel;
            //pictureBoxImage.MouseEnter += (s, e) => pictureBoxImage.Focus();   // 휠 포커스 확보

            numCropSize.Value = DefaultCropSize;
        }

        // =====================================================
        // 초기화
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

        private void BtnReset_Click(object sender, EventArgs e) => ResetAll();
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

        private void NumCropSize_ValueChanged(object sender, EventArgs e)
        {
            if (clickState != ClickState.TwoPoints)
                return;

            UpdateLineInfo();
            pictureBoxImage.Invalidate();
        }

        // =====================================================
        // 이미지 로딩
        // =====================================================
        private async void BtnLoadImage_Click(object sender, EventArgs e)
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
        private Bitmap ResizeToFit(Bitmap src, int maxW, int maxH)
        {
            double scale = Math.Min((double)maxW / src.Width, (double)maxH / src.Height);   // 비율 더 작은걸루
            Bitmap dst = new Bitmap((int)(src.Width * scale), (int)(src.Height * scale));

            using (Graphics g = Graphics.FromImage(dst))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;   // 보간 방식 설정(화질 최우선)
                g.DrawImage(src, 0, 0, dst.Width, dst.Height);
            }
            return dst;
        }

        // =====================================================
        // 마우스 이벤트
        // =====================================================
        private void PictureBoxImage_MouseDown(object sender, MouseEventArgs e)
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
                        PointF first_viewPt = OriginalToView(firstOriginalPt);
                        DPoint first_screenPt = ViewToScreen(first_viewPt);

                        if (IsHit(e.Location, first_screenPt))
                        {
                            isDraggingPoint = true;
                            draggingFirstPoint = true;
                            return;
                        }

                        if (clickState == ClickState.TwoPoints)
                        {
                            PointF second_viewPt = OriginalToView(secondOriginalPt);
                            DPoint second_screenPt = ViewToScreen(second_viewPt);

                            if (IsHit(e.Location, second_screenPt))
                            {
                                isDraggingPoint = true;
                                draggingSecondPoint = true;
                                return;
                            }
                        }
                    }

                    // 점 새로 찍기
                    PointF mouseViewPt = ScreenToView(e.Location);
                    PointF mouseOriginalPt = ViewToOriginal(mouseViewPt);

                    if (clickState == ClickState.None)
                    {
                        firstOriginalPt = mouseOriginalPt;
                        clickState = ClickState.OnePoint;
                    }
                    else if (clickState == ClickState.OnePoint)
                    {
                        secondOriginalPt = mouseOriginalPt;
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

        private void PictureBoxImage_MouseMove(object sender, MouseEventArgs e)
        {
            // 이미지 이동(패닝)
            if (isPanning)
            {
                viewOffset.X += e.X - lastMousePt.X;
                viewOffset.Y += e.Y - lastMousePt.Y;    // 이동 거리 계산
                lastMousePt = e.Location;               // (이동한 위치로) 기준점 갱신
                pictureBoxImage.Invalidate();
                return;
            }

            // 점 위치 이동(드래그)
            if (isDraggingPoint)
            {
                PointF view_Pt = ScreenToView(e.Location);
                PointF originalPt = ViewToOriginal(view_Pt);

                if (draggingFirstPoint)
                    firstOriginalPt = originalPt;

                else if (draggingSecondPoint)
                    secondOriginalPt = originalPt;

                UpdateLineInfo();
                pictureBoxImage.Invalidate();
            }

            // 마우스 포지션 좌표
            if (viewBitmap == null)
            {
                showMousePos = false;   
                return;
            }

            if (!IsInsideImageScreen(e.Location))  // 이미지 영역 밖이면
            {
                showMousePos = false;
                pictureBoxImage.Invalidate();
                return;
            }

            mouseScreenPt = e.Location;

            PointF viewPt = ScreenToView(e.Location);
            mouseOriginalPt = ViewToOriginal(viewPt);   // 이미지 좌표를 출력하기 위해

            showMousePos = true;
            pictureBoxImage.Invalidate();   // 다시 그리게 요청
        }


        private void PictureBoxImage_MouseUp(object sender, MouseEventArgs e)
        {
            isPanning = false;
            isDraggingPoint = false;
            draggingFirstPoint = false;
            draggingSecondPoint = false;
        }
        private void PictureBoxImage_MouseWheel(object sender, MouseEventArgs e)
        {
            float oldScale = viewScale;   // 확대 전 스케일 저장

            viewScale = e.Delta > 0 ? viewScale * ZoomStep : viewScale / ZoomStep;   // 휠 한칸당 10% 확대/축소
            viewScale = Math.Max(MinZoom, Math.Min(MaxZoom, viewScale));  // 줌 한계 제한

            // 마우스 위치를 기준으로 이미지 다시 배치
            viewOffset.X = e.X - (e.X - viewOffset.X) * (viewScale / oldScale);   // e.X - (기존 거리 × 확대비율)
            viewOffset.Y = e.Y - (e.Y - viewOffset.Y) * (viewScale / oldScale);

            pictureBoxImage.Invalidate();
        }

        // =====================================================
        // Paint(점,선,가이드박스)
        // =====================================================
        private void PictureBoxImage_Paint(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Color.Black);   // 이전 화면 초기화
            g.SmoothingMode = SmoothingMode.AntiAlias;   // 선,원,텍스트 부드럽게

            if (isImageLoading)  // 로딩중
            {
                DrawLoadingSpinner(g);
                return;
            }

            if (viewBitmap == null)
                return;

            g.TranslateTransform(viewOffset.X, viewOffset.Y);   // 좌표계 이동(중앙정렬, 드래그 이동) 이미지가 시작하는 위치
            g.ScaleTransform(viewScale, viewScale);             // 좌표계 스케일 변경(확대/축소 적용)

            g.DrawImage(viewBitmap, 0, 0);                      // 이미지 그리기

            DrawPointsAndLine(g);                               // 점, 선 그리기
            DrawGuideBoxes(g);                                  // 가이드 박스 그리기

            g.ResetTransform();                                 // 좌표계 원복

            if (showMousePos)
            {
                DrawMousePositionOverlay(g);
            }

        }
        private void DrawMousePositionOverlay(Graphics g)   // 마우스 포지션 점좌표 그리기
        {
            string text = $"({(int)mouseOriginalPt.X}, {(int)mouseOriginalPt.Y})";

            using (Font font = new Font("맑은 고딕", 9, FontStyle.Bold))
            {
                SizeF textSize = g.MeasureString(text, font);

                int padding = 4;  // 텍스트와 배경 사이 여백
                float x = mouseScreenPt.X + 12;   // 마우스 커서랑 겹치지 않게
                float y = mouseScreenPt.Y + 12;

                RectangleF bgRect = new RectangleF(   // 배경 사각형 크기
                    x,
                    y,
                    textSize.Width + padding * 2,
                    textSize.Height + padding * 2
                );

                // 반투명 배경
                using (Brush bg = new SolidBrush(Color.FromArgb(180, 0, 0, 0)))
                    g.FillRectangle(bg, bgRect);

                // 텍스트
                g.DrawString(
                    text,
                    font,
                    Brushes.DeepSkyBlue,
                    x + padding,
                    y + padding
                );
            }
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

        private List<RectangleF> CalculateCropBoxesOriginal()  // 크롭할 사각형 목록 만듬
        {
            List<RectangleF> boxes = new List<RectangleF>();
            if (clickState != ClickState.TwoPoints)
                return boxes;

            int cropSize = (int)numCropSize.Value;

            float dx = secondOriginalPt.X - firstOriginalPt.X;    // 방향백터
            float dy = secondOriginalPt.Y - firstOriginalPt.Y;
            float length = (float)Math.Sqrt(dx * dx + dy * dy);   // 선길이
            if (length < 1f)
                return boxes;

            float ux = dx / length;
            float uy = dy / length;   // 단위방향

            for (float dist = 0; dist <= length + cropSize / 2f; dist += cropSize)
            {
                // 선을 따라 이동한 기준점 (Anchor)
                PointF anchor = new PointF(
                    firstOriginalPt.X + ux * dist,
                    firstOriginalPt.Y + uy * dist);

                // 기준점(좌상단, 우상단, 센터 등) 어딘지 구함
                PointF tl = AnchorToBox(anchor, cropSize);

                boxes.Add(new RectangleF(tl.X, tl.Y, cropSize, cropSize));
            }

            return boxes;
        }

        /* =========================================================
        *  크롭 저장
         * ========================================================= */
        private void BtnCropSave_Click(object sender, EventArgs e) => CropAndSaveAll();

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
        // 좌표 관련
        // =====================================================
        private PointF ViewToOriginal(PointF viewPt)
        {
            float sx = (float)originalBitmap.Width / viewBitmap.Width;
            float sy = (float)originalBitmap.Height / viewBitmap.Height;

            return new PointF(
                viewPt.X * sx,
                viewPt.Y * sy
            );
        }

        private PointF OriginalToView(PointF originalPt)
        {
            float sx = (float)viewBitmap.Width / originalBitmap.Width;
            float sy = (float)viewBitmap.Height / originalBitmap.Height;

            return new PointF(
                originalPt.X * sx,
                originalPt.Y * sy
            );
        }

        private PointF ScreenToView(DPoint screenPt)
        {
            return new PointF(
                (screenPt.X - viewOffset.X) / viewScale,
                (screenPt.Y - viewOffset.Y) / viewScale
            );
        }

        private DPoint ViewToScreen(PointF viewPt)
        {
            float x = viewPt.X * viewScale + viewOffset.X;
            float y = viewPt.Y * viewScale + viewOffset.Y;

            return new DPoint(
                (int)Math.Round(x),
                (int)Math.Round(y)
            );
        }

        private bool IsHit(DPoint mousePt, DPoint targetPt)
        {
            return Math.Abs(mousePt.X - targetPt.X) <= HitRadius &&
                   Math.Abs(mousePt.Y - targetPt.Y) <= HitRadius;
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

        // =====================================================
        // UI 관련
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