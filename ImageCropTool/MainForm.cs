using OpenCvSharp;
using OpenCvSharp.Extensions;
using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ImageCropTool
{
    public partial class MainForm : Form
    {
        // ==============================
        //  이미지 데이터
        // ==============================
        private Bitmap originalBitmap = null;
        private Mat originalMat = null;
        private Bitmap viewBitmap = null;

        // 좌표 변환 스케일
        private float scaleX = 1.0f;
        private float scaleY = 1.0f;

        // ==============================
        //  클릭 / 드래그 상태
        // ==============================
        private enum ClickState { None, OnePoint, TwoPoints }
        private ClickState clickState = ClickState.None;

        private bool isDragging = false;
        private bool draggingFirstPoint = false;
        private bool draggingSecondPoint = false;

        private const int HitTestRadius = 8;

        // View 좌표
        private System.Drawing.Point firstViewPt;
        private System.Drawing.Point secondViewPt;

        // Original 좌표
        private PointF firstOriginalPt;
        private PointF secondOriginalPt;

        // 선 정보
        private float lineLength = 0f;
        private int cropCount = 0;


        // ==============================
        public MainForm()
        {
            InitializeComponent();

            pictureBoxImage.Paint += pictureBoxImage_Paint;
            pictureBoxImage.MouseDown += pictureBoxImage_MouseDown;
            pictureBoxImage.MouseMove += pictureBoxImage_MouseMove;
            pictureBoxImage.MouseUp += pictureBoxImage_MouseUp;
        }

        // ==============================
        //  이미지 로드
        // ==============================
        private async void btnLoadImage_Click(object sender, EventArgs e)
        {
            OpenFileDialog dlg = new OpenFileDialog();
            dlg.Filter = "Image Files|*.bmp;*.jpg;*.png";

            if (dlg.ShowDialog() != DialogResult.OK)
                return;

            btnLoadImage.Enabled = false;
            this.Text = "이미지 로딩 중...";

            try
            {
                Bitmap preview = null;

                await Task.Run(() =>
                {
                    using (Bitmap full = new Bitmap(dlg.FileName))
                    {
                        preview = ResizeToFit(
                            full,
                            pictureBoxImage.Width,
                            pictureBoxImage.Height);
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

                scaleX = (float)originalBitmap.Width / viewBitmap.Width;
                scaleY = (float)originalBitmap.Height / viewBitmap.Height;

                ResetPoints();
            }
            finally
            {
                btnLoadImage.Enabled = true;
                this.Text = "ImageCropTool";
            }
        }

        // ==============================
        //  좌표 변환
        // ==============================
        private PointF ViewToOriginal(System.Drawing.Point viewPt)
        {
            return new PointF(
                viewPt.X * scaleX,
                viewPt.Y * scaleY);
        }

        // 선길이 계산 함수(Original 기준)
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
            cropCount = (int)(lineLength / cropSize);

            lblLineLength.Text = $"Line Length: {lineLength:F1}px";
            lblCropCount.Text = $"Crop Count: {cropCount}";
        }


        // ==============================
        //  마우스 Down
        // ==============================
        private void pictureBoxImage_MouseDown(object sender, MouseEventArgs e)
        {
            if (viewBitmap == null)
                return;

            //  점 히트 테스트 (드래그 시작)
            if (clickState != ClickState.None)
            {
                if (IsHit(e.Location, firstViewPt))
                {
                    isDragging = true;
                    draggingFirstPoint = true;
                    return;
                }
                if (clickState == ClickState.TwoPoints &&
                    IsHit(e.Location, secondViewPt))
                {
                    isDragging = true;
                    draggingSecondPoint = true;
                    return;
                }
            }

            //  클릭 로직
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
                //  3번째 클릭 → 초기화 후 새 시작
                ResetPoints();

                firstViewPt = e.Location;
                firstOriginalPt = ViewToOriginal(firstViewPt);
                clickState = ClickState.OnePoint;
            }

            pictureBoxImage.Invalidate();
        }

        // ==============================
        //  마우스 Move (드래그)
        // ==============================
        private void pictureBoxImage_MouseMove(object sender, MouseEventArgs e)
        {
            if (!isDragging)
                return;

            if (draggingFirstPoint)
            {
                firstViewPt = e.Location;
                firstOriginalPt = ViewToOriginal(firstViewPt);
                UpdateLineInfo();
            }
            else if (draggingSecondPoint)
            {
                secondViewPt = e.Location;
                secondOriginalPt = ViewToOriginal(secondViewPt);
                UpdateLineInfo();
            }

            pictureBoxImage.Invalidate();
        }

        // ==============================
        //  마우스 Up
        // ==============================
        private void pictureBoxImage_MouseUp(object sender, MouseEventArgs e)
        {
            isDragging = false;
            draggingFirstPoint = false;
            draggingSecondPoint = false;
        }

        // ==============================
        //  Paint
        // ==============================
        private void pictureBoxImage_Paint(object sender, PaintEventArgs e)
        {
            if (viewBitmap == null || clickState == ClickState.None)
                return;

            using (Pen pen = new Pen(Color.Red, 2))
            {
                DrawPoint(e.Graphics, firstViewPt);

                if (clickState == ClickState.TwoPoints)
                {
                    DrawPoint(e.Graphics, secondViewPt);
                    e.Graphics.DrawLine(pen, firstViewPt, secondViewPt);
                }
            }
        }

        // ==============================
        //  유틸
        // ==============================
        private bool IsHit(System.Drawing.Point p, System.Drawing.Point target)
        {
            return Math.Abs(p.X - target.X) <= HitTestRadius &&
                   Math.Abs(p.Y - target.Y) <= HitTestRadius;
        }

        private void DrawPoint(Graphics g, System.Drawing.Point pt)
        {
            g.FillEllipse(
                Brushes.Red,
                pt.X - 4, pt.Y - 4,
                8, 8);
        }

        private void ResetPoints()
        {
            clickState = ClickState.None;
            firstViewPt = System.Drawing.Point.Empty;
            secondViewPt = System.Drawing.Point.Empty;
            pictureBoxImage.Invalidate();
        }

        private Bitmap ResizeToFit(Bitmap src, int maxW, int maxH)
        {
            double scale = Math.Min(
                (double)maxW / src.Width,
                (double)maxH / src.Height);

            int newW = Math.Max(1, (int)(src.Width * scale));
            int newH = Math.Max(1, (int)(src.Height * scale));

            Bitmap dst = new Bitmap(newW, newH);

            using (Graphics g = Graphics.FromImage(dst))
            {
                g.InterpolationMode =
                    System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;

                // ✅ src → dst
                g.DrawImage(src, 0, 0, newW, newH);
            }

            return dst;
        }

        private void numCropSize_ValueChanged(object sender, EventArgs e)
        {
            UpdateLineInfo();
        }
    }
}
