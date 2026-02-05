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
        /* =========================================================
         *  Crop Box
         * ========================================================= */
        private List<CropBoxInfo> cropBoxes = new List<CropBoxInfo>();
        private CropBoxInfo hoveredBox = null;   // 지금 hover중인 대상

        /* =========================================================
         *  Line / Crop Info
         * ========================================================= */
        private float lineLength = 0f;
        private int cropCount = 0;
        private const int DefaultCropSize = 512;

        /* =========================================================
         *  Image
         * ========================================================= */
        private Bitmap viewBitmap;
        private Bitmap originalBitmap;
        private Mat originalMat;

        /* =========================================================
         *  Loading Spinner
         * ========================================================= */
        private bool isImageLoading = false;
        private Timer loadingTimer;
        private float spinnerAngle = 0f;

        /* =========================================================
         *  Drag / Click State
         * ========================================================= */
        private bool isDraggingPoint = false;
        private bool draggingFirstPoint = false;
        private bool draggingSecondPoint = false;
        private const int HitRadius = 8;

        private enum ClickState { None, OnePoint, TwoPoints }
        private ClickState clickState = ClickState.None;

        /* =========================================================
         *  Points
         * ========================================================= */
        private PointF firstOriginalPt;
        private PointF secondOriginalPt;

        /* =========================================================
         *  Mouse Position Display
         * ========================================================= */
        private PointF mouseOriginalPt;    // 표시할 이미지 좌표
        private DPoint mouseScreenPt;      // 텍스트를 그릴 화면 위치

        /* =========================================================
         *  Crop Anchor
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
         *  View Transform (Zoom & Pan)
         * ========================================================= */
        private float viewScale = 1.0f;                 // 줌 배율
        private PointF viewOffset = new PointF(0, 0);   // 이미지 시작 위치

        private const float ZoomStep = 1.1f;            // 휠 한칸에 10%씩 변화
        private const float MinZoom = 0.2f;
        private const float MaxZoom = 5.0f;

        private bool isPanning = false;
        private DPoint lastMousePt;

        /* =========================================================
         *  Constructor
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

            pictureBoxImage.SizeMode = PictureBoxSizeMode.Normal;
            pictureBoxImage.Paint += PictureBoxImage_Paint;
            pictureBoxImage.MouseDown += PictureBoxImage_MouseDown;
            pictureBoxImage.MouseMove += PictureBoxImage_MouseMove;
            pictureBoxImage.MouseUp += PictureBoxImage_MouseUp;
            pictureBoxImage.MouseWheel += PictureBoxImage_MouseWheel;

            pictureBoxPreview.SizeMode = PictureBoxSizeMode.Zoom;
            numCropSize.Value = DefaultCropSize;
        }

        /* =========================================================
         *  Reset
         * ========================================================= */
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
            firstOriginalPt = PointF.Empty;
            secondOriginalPt = PointF.Empty;

            lineLength = 0f;
            cropCount = 0;

            cropBoxes.Clear();
            hoveredBox = null;
            ClearPreview();

            lblLineLength.Text = "Line Length: -";
            lblCropCount.Text = "Crop Count: -";

            pictureBoxImage.Invalidate();
        }

        private void ResetView()   // 이미지 출력 위치 초기화
        {
            viewScale = 1.0f;

            if (viewBitmap != null)
            {
                viewOffset = new PointF(    // 이미지 중앙에 오게
                    (pictureBoxImage.Width - viewBitmap.Width) /2f,
                    (pictureBoxImage.Height - viewBitmap.Height) / 2f 
                );
            }
            else
            {
                viewOffset = new PointF(0, 0);  // 이미지 없으면
            }
        }

        private void NumCropSize_ValueChanged(object sender, EventArgs e)
        {
            if (clickState != ClickState.TwoPoints)
                return;
            CalculateCropBox();
            UpdateLineInfo();
            pictureBoxImage.Invalidate();
        }

        /* =========================================================
         *  Image Load
         * ========================================================= */
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

        /* =========================================================
         *  Mouse Down
         * ========================================================= */
        private void PictureBoxImage_MouseDown(object sender, MouseEventArgs e)
        {
            if (viewBitmap == null)
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
                    
                    // 기존점 드래그?
                    if (clickState != ClickState.None)
                    {
                        PointF firstView = OriginalToView(firstOriginalPt);
                        if (IsHit(e.Location, ViewToScreen(firstView)))
                        {
                            isDraggingPoint = true;
                            draggingFirstPoint = true;
                            return;
                        }

                        if (clickState == ClickState.TwoPoints)
                        {
                            PointF secondView = OriginalToView(secondOriginalPt);
                            if (IsHit(e.Location, ViewToScreen(secondView)))
                            {
                                isDraggingPoint = true;
                                draggingSecondPoint = true;
                                return;
                            }
                        }
                    }

                    // 화면좌표 -> 오리지널 좌표로 변환
                    PointF viewPt = ScreenToView(e.Location);
                    PointF originalPt = ViewToOriginal(viewPt);

                    if (clickState == ClickState.None)
                    {
                        firstOriginalPt = originalPt;
                        clickState = ClickState.OnePoint;  // 첫번째 점
                    }
                    else if (clickState == ClickState.OnePoint)
                    {
                        secondOriginalPt = originalPt;
                        clickState = ClickState.TwoPoints;   // 두번째 점
                        CalculateCropBox();
                        UpdateLineInfo();
                    }
                    else
                    {
                        ClearPoints();  // 초기화
                    }
                    pictureBoxImage.Invalidate();
                    break;
            }            
        }

        /* =========================================================
         *  Mouse Move
         * ========================================================= */
        private void PictureBoxImage_MouseMove(object sender, MouseEventArgs e)
        {
            if (isPanning)    // 이미지 이동
            {
                viewOffset.X += e.X - lastMousePt.X;
                viewOffset.Y += e.Y - lastMousePt.Y;    // 이동거리 계산
                lastMousePt = e.Location;               // (이동한 위치로) 기준점 갱신
                pictureBoxImage.Invalidate();
                return;
            }

            if (isDraggingPoint)  // 점 드래그
            {
                PointF viewPt = ScreenToView(e.Location);
                PointF originalPt = ViewToOriginal(viewPt);

                if (draggingFirstPoint)
                    firstOriginalPt = originalPt;
                else if (draggingSecondPoint)
                    secondOriginalPt = originalPt;

                CalculateCropBox();
                UpdateLineInfo();
                pictureBoxImage.Invalidate();
                return;
            }

            if (!IsInsideImageScreen(e.Location))
                return;

            mouseScreenPt = e.Location;   // 화면에 좌표 표시용
            mouseOriginalPt = ViewToOriginal(ScreenToView(e.Location));  // hover 판정용, preview 대상 결정용

            UpdateHoverCropBox(mouseOriginalPt);  // hover 박스 결정, highlight 갱신, preview 갱신
            pictureBoxImage.Invalidate();
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
            viewScale = e.Delta > 0 ? viewScale * ZoomStep : viewScale / ZoomStep;      // 휠 한칸당 10% 확대/축소
            viewScale = Math.Max(MinZoom, Math.Min(MaxZoom, viewScale));   // 줌 한계 제한

            // 마우스 위치를 기준으로 이미지 다시 배치
            viewOffset.X = e.X - (e.X - viewOffset.X) * (viewScale / oldScale);   // e.X - (기존 거리 × 확대비율)
            viewOffset.Y = e.Y - (e.Y - viewOffset.Y) * (viewScale / oldScale);

            pictureBoxImage.Invalidate();
        }

        /* =========================================================
         *  Paint
         * ========================================================= */
        private void PictureBoxImage_Paint(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Color.Black);      // 이전 화면 초기화
            g.SmoothingMode = SmoothingMode.AntiAlias;   // 선,원,텍스트 부드럽게

            if (isImageLoading)
            {
                DrawLoadingSpinner(g);
                return;
            }

            if (viewBitmap == null)
                return;

            g.TranslateTransform(viewOffset.X, viewOffset.Y);    // 좌표계 이동(중앙정렬, 드래그 이동) 이미지가 시작하는 위치
            g.ScaleTransform(viewScale, viewScale);              // 좌표계 스케일 변경(확대/축소 적용)

            g.DrawImage(viewBitmap, 0, 0);                       // 이미지 그리기
            DrawPointsAndLine(g);                                // 점, 선 그리기
            DrawGuideBoxes(g);                                   // 가이드 박스 그리기

            g.ResetTransform();                                  // 좌표계 원복
            DrawMousePositionOverlay(g);                         // 마우스 포지션
        }

        /* =========================================================
         *  Draw Helpers
         * ========================================================= */
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
                using (Brush b = new SolidBrush(Color.FromArgb(180, 0, 0, 0)))
                    g.FillRectangle(b, bg);

                g.DrawString(text, font, Brushes.DeepSkyBlue, x + 4, y + 4);    // 텍스트
            }
        }

        private void DrawPointsAndLine(Graphics g)    // 점&선 그리기
        {
            using (Pen pen = new Pen(Color.Red, 2 / viewScale))   // 확대/축소 해도 같은 두깨로 역보정
            {
                if (clickState != ClickState.None)
                    DrawPoint(g, firstOriginalPt);        // 첫 점 그림

                if (clickState == ClickState.TwoPoints)
                {
                    DrawPoint(g, secondOriginalPt);      // 두번째 점 그림
                    g.DrawLine(                          // 선 그림
                        pen,
                        OriginalToView(firstOriginalPt),
                        OriginalToView(secondOriginalPt)
                    );
                }
            }
        }

        private void DrawPoint(Graphics g, PointF originalPt)
        {
            PointF pt = OriginalToView(originalPt);     // 화면 좌표로
            float r = 4 / viewScale;        // 반지름 보정(확대/축소 해도 똑같게 역보정)
            g.FillEllipse(Brushes.Red, pt.X - r, pt.Y - r, r * 2, r * 2);  // 좌상단 기준이기 때문에 중심으로 보정
        }

        private void DrawGuideBoxes(Graphics g)
        {
            foreach (var box in cropBoxes)
            {
                Color color = box.IsHovered ? Color.Lime : Color.Yellow;

                using (Pen pen = new Pen(color, 2 / viewScale)  // 선 두께 보정
                { DashStyle = DashStyle.Dash })
                {
                    Rectangle r = box.EffectiveRect;

                    PointF tl = OriginalToView(new PointF(r.Left, r.Top));
                    PointF br = OriginalToView(new PointF(r.Right, r.Bottom));

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

        /* =========================================================
         *  크롭박스 계산 / 기준점
         * ========================================================= */
        private void CalculateCropBox()    // 크롭박스들 계산
        {
            cropBoxes.Clear();

            if (clickState != ClickState.TwoPoints)
                return;

            int cropSize = (int)numCropSize.Value;

            float dx = secondOriginalPt.X - firstOriginalPt.X;
            float dy = secondOriginalPt.Y - firstOriginalPt.Y;
            float length = (float)Math.Sqrt(dx * dx + dy * dy);

            if (length < 1f)
                return;

            float ux = dx / length;
            float uy = dy / length;

            for (float dist = 0; dist <= length + cropSize / 2f; dist += cropSize)
            {
                PointF anchor = new PointF(   // 선을 따라 이동한 기준점
                    firstOriginalPt.X + ux * dist,
                    firstOriginalPt.Y + uy * dist
                );

                PointF tl = AnchorToBox(anchor, cropSize);  // 실제 사각형의 좌상단 계산

                int x = (int)Math.Max(0, Math.Min(tl.X, originalMat.Width - cropSize));    // 이미지 밖으로 크롭 못나가게 
                int y = (int)Math.Max(0, Math.Min(tl.Y, originalMat.Height - cropSize));   // 경계 보정

                cropBoxes.Add(new CropBoxInfo
                {
                    EffectiveRect = new Rectangle(x, y, cropSize, cropSize)
                });
            }
        }

        private PointF AnchorToBox(PointF anchor, float size)
        {
            switch (cropAnchor)
            {
                case CropAnchor.Center:
                    return new PointF(anchor.X - size / 2f, anchor.Y - size / 2f);
                case CropAnchor.TopLeft:
                    return anchor;
                case CropAnchor.TopRight:
                    return new PointF(anchor.X - size, anchor.Y);
                case CropAnchor.BottomLeft:
                    return new PointF(anchor.X, anchor.Y - size);
                case CropAnchor.BottomRight:
                    return new PointF(anchor.X - size, anchor.Y - size);
                default:
                    return anchor;
            }
        }

        private void UpdateHoverCropBox(PointF originalPt)
        {
            hoveredBox = null;    // 초기화

            foreach (var box in cropBoxes)   // 마우스들어간 박스 찾기
            {
                if (box.EffectiveRect.Contains(
                        (int)originalPt.X,
                        (int)originalPt.Y))
                {
                    hoveredBox = box;
                    break;
                }
            }

            foreach (var box in cropBoxes)    // 모든 박스 hover 상태 갱신
                box.IsHovered = (box == hoveredBox);

            if (hoveredBox != null)
                ShowCropPreview(hoveredBox);
            else
                ClearPreview();
        }

        /* =========================================================
         *  Preview 미리보기
         * ========================================================= */
        private void ShowCropPreview(CropBoxInfo hoverdBox)   // 박스 미리보기
        {
            if (hoverdBox == null || originalMat == null)
                return;

            Rectangle r = hoverdBox.EffectiveRect;

            var roi = new OpenCvSharp.Rect(   // ROI 생성
                r.X, r.Y, r.Width, r.Height
            );

            using (Mat cropped = new Mat(originalMat, roi))   // ROI로 Mat 잘라내기
            {
                pictureBoxPreview.Image?.Dispose();
                pictureBoxPreview.Image = BitmapConverter.ToBitmap(cropped);
            }
        }

        private void ClearPreview()
        {
            pictureBoxPreview.Image?.Dispose();
            pictureBoxPreview.Image = null;
        }

        /* =========================================================
         *  크롭박스 저장
         * ========================================================= */
        private void BtnCropSave_Click(object sender, EventArgs e) => CropAndSaveAll();

        private void CropAndSaveAll()
        {
            if (cropBoxes.Count == 0)
                return;

            string folder = Path.Combine(
                Application.StartupPath,
                "Crops",
                DateTime.Now.ToString("yyyyMMdd_HHmmss")
            );
            Directory.CreateDirectory(folder);

            int index = 1;
            foreach (var box in cropBoxes)
            {
                Rectangle r = box.EffectiveRect;

                var roi = new OpenCvSharp.Rect(
                    r.X, r.Y, r.Width, r.Height
                );

                string path = Path.Combine(
                    folder,
                    $"crop_{index++:D3}.png"
                );

                using (Mat cropped = new Mat(originalMat, roi))
                {
                    Cv2.ImWrite(path, cropped);
                }
            }

            MessageBox.Show($"저장 완료: {index - 1}개");
        }

        /* =========================================================
         *  좌표 계산
         * ========================================================= */
        private PointF ViewToOriginal(PointF viewPt)
        {
            return new PointF(
                viewPt.X * originalBitmap.Width / viewBitmap.Width,
                viewPt.Y * originalBitmap.Height / viewBitmap.Height
            );
        }

        private PointF OriginalToView(PointF originalPt)
        {
            return new PointF(
                originalPt.X * viewBitmap.Width / originalBitmap.Width,
                originalPt.Y * viewBitmap.Height / originalBitmap.Height
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
            return new DPoint(
                (int)(viewPt.X * viewScale + viewOffset.X),
                (int)(viewPt.Y * viewScale + viewOffset.Y)
            );
        }

        private bool IsHit(DPoint mousePt, DPoint targetPt)
        {
            return Math.Abs(mousePt.X - targetPt.X) <= HitRadius &&
                   Math.Abs(mousePt.Y - targetPt.Y) <= HitRadius;
        }

        private bool IsInsideImageScreen(DPoint screenPt)
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

        /* =========================================================
         *  UI
         * ========================================================= */
        private void UpdateLineInfo()
        {
            float dx = secondOriginalPt.X - firstOriginalPt.X;
            float dy = secondOriginalPt.Y - firstOriginalPt.Y;

            lineLength = (float)Math.Sqrt(dx * dx + dy * dy);

            int cropSize = (int)numCropSize.Value;
            cropCount = (int)Math.Floor(
                (lineLength + cropSize / 2f) / cropSize
            ) + 1;

            lblLineLength.Text = $"Line Length: {lineLength:F1}px";
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
