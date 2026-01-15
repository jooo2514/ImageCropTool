using OpenCvSharp;
using OpenCvSharp.Extensions;

using System;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

//  Point 충돌 방지용 alias
using DPoint = System.Drawing.Point;
using DRect = System.Drawing.Rectangle;

namespace ImageCropTool
{
    public partial class MainForm : Form
    {
        /* =========================================================
         * 이미지 데이터
         * ========================================================= */
        private Bitmap originalBitmap;          // 실제 원본 이미지
        private Mat originalMat;                 // OpenCV 처리용 원본
        private Bitmap viewBitmap;               // 화면 표시용 축소 이미지

        // View ↔ Original 좌표 변환 비율
        private float scaleX = 1f;
        private float scaleY = 1f;

        private bool isImageLoading = false;     // 로딩 중 입력 차단

        /* =========================================================
         * 클릭 상태 관리
         * ========================================================= */
        private enum ClickState
        {
            None,
            OnePoint,
            TwoPoints
        }

        private ClickState clickState = ClickState.None;

        private bool isDragging = false;
        private bool draggingFirst = false;
        private bool draggingSecond = false;
        private bool invalidClickNotified = false;


        private const int HitRadius = 8;

        // View 좌표 (화면 기준)
        private DPoint firstViewPt;
        private DPoint secondViewPt;

        // Original 좌표 (원본 이미지 기준)
        private PointF firstOriginalPt;
        private PointF secondOriginalPt;

        // 계산 정보
        private float lineLength = 0f;
        private int cropCount = 0;

        /* =========================================================
         * 생성자
         * ========================================================= */
        public MainForm()
        {
            InitializeComponent();

            pictureBoxImage.Paint += pictureBoxImage_Paint;
            pictureBoxImage.MouseDown += pictureBoxImage_MouseDown;
            pictureBoxImage.MouseMove += pictureBoxImage_MouseMove;
            pictureBoxImage.MouseUp += pictureBoxImage_MouseUp;
        }

        /* =========================================================
         * 이미지 로드
         * ========================================================= */
        private async void btnLoadImage_Click(object sender, EventArgs e)
        {
            OpenFileDialog dlg = new OpenFileDialog();
            dlg.Filter = "Image Files|*.bmp;*.jpg;*.png";

            if (dlg.ShowDialog() != DialogResult.OK)
                return;

            isImageLoading = true;
            pictureBoxImage.Enabled = false;
            btnCropSave.Enabled = false;
            btnLoadImage.Enabled = false;
            Text = "이미지 로딩 중...";

            try
            {
                Bitmap preview = null;

                // 화면 표시용 이미지
                await Task.Run(() =>
                {
                    using (Bitmap full = new Bitmap(dlg.FileName))
                    {
                        preview = ResizeToFit(full,
                            pictureBoxImage.Width,
                            pictureBoxImage.Height);
                    }
                });

                viewBitmap?.Dispose();
                viewBitmap = preview;
                pictureBoxImage.Image = viewBitmap;

                // 원본 이미지 + OpenCV Mat
                await Task.Run(() =>
                {
                    originalBitmap?.Dispose();
                    originalMat?.Dispose();

                    originalBitmap = new Bitmap(dlg.FileName);   // 원본 저장
                    originalMat = BitmapConverter.ToMat(originalBitmap);  // OpenCV용으로 복사
                });

                // 좌표 변환 비율 계산
                scaleX = (float)originalBitmap.Width / viewBitmap.Width;
                scaleY = (float)originalBitmap.Height / viewBitmap.Height;

                ResetAll();
            }
            catch (Exception ex)
            {
                MessageBox.Show("이미지 로드 실패\n" + ex.Message);
            }
            finally
            {
                isImageLoading = false;
                pictureBoxImage.Enabled = true;
                btnCropSave.Enabled = true;
                btnLoadImage.Enabled = true;
                Text = "ImageCropTool";
            }
        }

        /* =========================================================
         * 좌표 변환
         * ========================================================= */
        private PointF ViewToOriginal(DPoint pt)
        {
            DRect rect = GetImageViewRect();
            if (!rect.Contains(pt))
                return PointF.Empty;



            float rx = (float)(pt.X - rect.X) / rect.Width;
            float ry = (float)(pt.Y - rect.Y) / rect.Height;

            return new PointF(
                rx * originalBitmap.Width,
                ry * originalBitmap.Height);
        }

        private DPoint OriginalToView(PointF pt)
        {
            DRect rect = GetImageViewRect();

            float rx = pt.X / originalBitmap.Width;
            float ry = pt.Y / originalBitmap.Height;

            return new DPoint(
                rect.X + (int)(rx * rect.Width),
                rect.Y + (int)(ry * rect.Height));
        }

        /* =========================================================
         * 마우스 이벤트
         * ========================================================= */
        private void pictureBoxImage_MouseDown(object sender, MouseEventArgs e)
        {

            if (isImageLoading || viewBitmap == null)
                return;

            Rectangle imgRect = GetImageViewRect();

            if (e.Button == MouseButtons.Left && !imgRect.Contains(e.Location))
            {
                if (!invalidClickNotified)
                {
                    invalidClickNotified = true;
                    MessageBox.Show("올바른 영역을 클릭하세요.");
                }
                return;
            }

            // 정상 클릭 진입 지점
            invalidClickNotified = false;

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

            if (clickState == ClickState.None)   // 아무 점도 없다면
            {
                firstViewPt = e.Location;   // 지금 누른 곳을 1번 점
                firstOriginalPt = ViewToOriginal(firstViewPt);  // 원본 위치 계산
                clickState = ClickState.OnePoint;    // 점 하나 찍힘 상태 변경
            }
            else if (clickState == ClickState.OnePoint)
            {
                secondViewPt = e.Location;   // 지금 누른 곳을 2번 점
                secondOriginalPt = ViewToOriginal(secondViewPt);  // 원본 위치 게산
                clickState = ClickState.TwoPoints;  // 두 점 다 찍힘 상태변경
                UpdateLineInfo();
            }
            else
            {
                ResetAll();
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
         * 화면 출력
         * ========================================================= */
        private void pictureBoxImage_Paint(object sender, PaintEventArgs e)
        {
            if (isImageLoading || viewBitmap == null)
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
         * 가이드 박스
         * ========================================================= */
        private void DrawGuideBoxes(Graphics g)
        {
            if (clickState != ClickState.TwoPoints)
                return;

            int cropSize = (int)numCropSize.Value;
            if (cropSize <= 0)
                return;

            float dx = secondOriginalPt.X - firstOriginalPt.X;    // 가로 거리
            float dy = secondOriginalPt.Y - firstOriginalPt.Y;    //  세로 거리
            float length = (float)Math.Sqrt(dx * dx + dy * dy);   // 전체 거리

            if (length < 1)
                return;

            float ux = dx / length;
            float uy = dy / length;

            using (Pen pen = new Pen(Color.Yellow, 2))
            {
                pen.DashStyle = System.Drawing.Drawing2D.DashStyle.Dash;

                for (float dist = 0; dist <= length; dist += cropSize)
                {
                    float cx = firstOriginalPt.X + ux * dist;
                    float cy = firstOriginalPt.Y + uy * dist;

                    DPoint center = OriginalToView(new PointF(cx, cy));
                    int half = (int)((cropSize / 2f) / scaleX);

                    g.DrawRectangle(pen,
                        center.X - half,
                        center.Y - half,
                        half * 2,
                        half * 2);
                }
            }
        }

        /* =========================================================
         * 정보 갱신
         * ========================================================= */
        private void UpdateLineInfo()
        {
            if (clickState != ClickState.TwoPoints)
            {
                lblLineLength.Text = "Line Length: -";
                lblCropCount.Text = "Crop Count: -";
                return;
            }

            float dx = secondOriginalPt.X - firstOriginalPt.X;
            float dy = secondOriginalPt.Y - firstOriginalPt.Y;

            lineLength = (float)Math.Sqrt(dx * dx + dy * dy);
            int cropSize = (int)numCropSize.Value;

            cropCount = (int)Math.Ceiling(lineLength / cropSize);

            lblLineLength.Text = $"Line Length: {lineLength:F1}px";
            lblCropCount.Text = $"Crop Count: {cropCount}";
        }

        private void numCropSize_ValueChanged(object sender, EventArgs e)
        {
            UpdateLineInfo();
            pictureBoxImage.Invalidate();
        }

        /* =========================================================
         * 유틸
         * ========================================================= */
        private bool IsHit(DPoint p, DPoint target)
        {
            return Math.Abs(p.X - target.X) <= HitRadius &&
                   Math.Abs(p.Y - target.Y) <= HitRadius;
        }

        private void DrawPoint(Graphics g, DPoint pt)
        {
            g.FillEllipse(Brushes.Red, pt.X - 4, pt.Y - 4, 8, 8);
        }

        private void ResetAll()
        {
            clickState = ClickState.None;
            firstViewPt = DPoint.Empty;
            secondViewPt = DPoint.Empty;

            lblLineLength.Text = "Line Length: -";
            lblCropCount.Text = "Crop Count: -";

            pictureBoxImage.Invalidate();
        }

        private Bitmap ResizeToFit(Bitmap src, int maxW, int maxH)
        {
            double scale = Math.Min(
                (double)maxW / src.Width,
                (double)maxH / src.Height);

            Bitmap dst = new Bitmap(
                (int)(src.Width * scale),
                (int)(src.Height * scale));

            using (Graphics g = Graphics.FromImage(dst))
            {
                g.InterpolationMode =
                    System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(src, 0, 0, dst.Width, dst.Height);
            }
            return dst;
        }

        /* =========================================================
         * Crop & Save
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
            if (cropSize <= 0)
                return;

            float dx = secondOriginalPt.X - firstOriginalPt.X;
            float dy = secondOriginalPt.Y - firstOriginalPt.Y;
            float length = (float)Math.Sqrt(dx * dx + dy * dy);

            float ux = dx / length;
            float uy = dy / length;

            int total = (int)Math.Ceiling(length / cropSize);

            string folder = Path.Combine(
                Application.StartupPath,
                "Crops",
                DateTime.Now.ToString("yyyyMMdd_HHmmss"));

            Directory.CreateDirectory(folder);

            int index = 1;

            for (int i = 0; i < total; i++)
            {
                float dist = Math.Min(i * cropSize, length);
                CropOne(dist, ux, uy, cropSize, folder, ref index);
            }

            MessageBox.Show($"Crop 저장 완료 ({index - 1}개)");
        }

        private void CropOne(
            float dist,
            float ux,
            float uy,
            int cropSize,
            string folder,
            ref int index)
        {
            // cx, cy : 자를 중심점 위치
            float cx = firstOriginalPt.X + ux * dist;
            float cy = firstOriginalPt.Y + uy * dist;
            // x, y : 자를 사각형의 왼쪽 상단 모서리 구하기
            int x = (int)Math.Round(cx - cropSize / 2f);
            int y = (int)Math.Round(cy - cropSize / 2f);

            x = Math.Max(0, x);
            y = Math.Max(0, y);

            int w = Math.Min(cropSize, originalMat.Width - x);
            int h = Math.Min(cropSize, originalMat.Height - y);

            if (w <= 0 || h <= 0)
                return;

            // 자를 영역 정하기
            Rect roi = new Rect(x, y, w, h);

            // originalMat(원본)에서 roi(영역)만큼만 떼어내서 (Mat cropped) 파일로 저장
            using (Mat cropped = new Mat(originalMat, roi))
            {
                string path = Path.Combine(folder, $"crop_{index:D3}.png");
                Cv2.ImWrite(path, cropped);
                index++;
            }
        }

        private DRect GetImageViewRect()
        {
            if (viewBitmap == null)
                return DRect.Empty;

            float imgRatio = (float)viewBitmap.Width / viewBitmap.Height;
            float boxRatio = (float)pictureBoxImage.Width / pictureBoxImage.Height;

            if (imgRatio > boxRatio)
            {
                int w = pictureBoxImage.Width;
                int h = (int)(w / imgRatio);
                int y = (pictureBoxImage.Height - h) / 2;
                return new DRect(0, y, w, h);
            }
            else
            {
                int h = pictureBoxImage.Height;
                int w = (int)(h * imgRatio);
                int x = (pictureBoxImage.Width - w) / 2;
                return new DRect(x, 0, w, h);
            }
        }
    }
}
