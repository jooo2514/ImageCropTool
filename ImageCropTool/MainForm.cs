using OpenCvSharp.Extensions;
using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.IO;

namespace ImageCropTool
{
    public partial class MainForm : Form
    {
        // =========================
        // 이미지 데이터
        // =========================
        private Bitmap originalBitmap = null;
        private OpenCvSharp.Mat originalMat = null;
        private Bitmap viewBitmap = null;

        private float scaleX = 1f;
        private float scaleY = 1f;

        private bool isImageLoading = false;


        // =========================
        // 클릭 / 드래그 상태
        // =========================
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

        private const int HitRadius = 8;

        // View 좌표
        private Point firstViewPt;
        private Point secondViewPt;

        // Original 좌표
        private PointF firstOriginalPt;
        private PointF secondOriginalPt;

        // =========================
        // 계산 정보
        // =========================
        private float lineLength = 0f;
        private int cropCount = 0;

        // =========================
        public MainForm()
        {
            InitializeComponent();

            pictureBoxImage.Paint += pictureBoxImage_Paint;
            pictureBoxImage.MouseDown += pictureBoxImage_MouseDown;
            pictureBoxImage.MouseMove += pictureBoxImage_MouseMove;
            pictureBoxImage.MouseUp += pictureBoxImage_MouseUp;
        }

        // =========================
        // 이미지 로드
        // =========================
        private async void btnLoadImage_Click(object sender, EventArgs e)
        {
            OpenFileDialog dlg = new OpenFileDialog();
            dlg.Filter = "Image Files|*.bmp;*.jpg;*.png";

            if (dlg.ShowDialog() != DialogResult.OK)
                return;

            isImageLoading = true;
            pictureBoxImage.Enabled = false;   // ⭐ 입력 차단
            btnCropSave.Enabled = false;

            btnLoadImage.Enabled = false;
            Text = "이미지 로딩 중...";

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

                ResetAll();
            }
            catch (Exception ex)
            {
                MessageBox.Show("이미지 로드 실패\n" + ex.Message);
            }
            finally
            {
                isImageLoading = false;
                pictureBoxImage.Enabled = true;   // ⭐ 입력 허용
                btnCropSave.Enabled = true;

                btnLoadImage.Enabled = true;
                Text = "ImageCropTool";
            }
        }


        // =========================
        // 좌표 변환
        // =========================
        private PointF ViewToOriginal(Point pt)
        {
            Rectangle imgRect = GetImageViewRect();
            if (!imgRect.Contains(pt))
                return PointF.Empty;

            float rx = (float)(pt.X - imgRect.X) / imgRect.Width;
            float ry = (float)(pt.Y - imgRect.Y) / imgRect.Height;

            return new PointF(
                rx * originalBitmap.Width,
                ry * originalBitmap.Height);
        }


        private Point OriginalToView(PointF pt)
        {
            Rectangle imgRect = GetImageViewRect();

            float rx = pt.X / originalBitmap.Width;
            float ry = pt.Y / originalBitmap.Height;

            return new Point(
                imgRect.X + (int)(rx * imgRect.Width),
                imgRect.Y + (int)(ry * imgRect.Height));
        }


        // =========================
        // 마우스 Down
        // =========================
        private void pictureBoxImage_MouseDown(object sender, MouseEventArgs e)
        {
            if (isImageLoading)
                return;
            if (viewBitmap == null)
                return;

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
                ResetAll();
                firstViewPt = e.Location;
                firstOriginalPt = ViewToOriginal(firstViewPt);
                clickState = ClickState.OnePoint;
            }

            pictureBoxImage.Invalidate();
        }

        // =========================
        // 마우스 Move (드래그)
        // =========================
        private void pictureBoxImage_MouseMove(object sender, MouseEventArgs e)
        {
            if (!isDragging)
                return;

            if (draggingFirst)
            {
                firstViewPt = e.Location;
                firstOriginalPt = ViewToOriginal(firstViewPt);
                UpdateLineInfo();
            }
            else if (draggingSecond)
            {
                secondViewPt = e.Location;
                secondOriginalPt = ViewToOriginal(secondViewPt);
                UpdateLineInfo();
            }

            pictureBoxImage.Invalidate();
        }

        private void pictureBoxImage_MouseUp(object sender, MouseEventArgs e)
        {
            isDragging = false;
            draggingFirst = false;
            draggingSecond = false;
        }

        // =========================
        // Paint
        // =========================
        private void pictureBoxImage_Paint(object sender, PaintEventArgs e)
        {
            if (isImageLoading)
                return;

            if (viewBitmap == null)
                return;

            using (Pen redPen = new Pen(Color.Red, 2))
            {
                if (clickState != ClickState.None)
                    DrawPoint(e.Graphics, firstViewPt);

                if (clickState == ClickState.TwoPoints)
                {
                    DrawPoint(e.Graphics, secondViewPt);
                    e.Graphics.DrawLine(redPen, firstViewPt, secondViewPt);
                }
            }

            DrawGuideBoxes(e.Graphics);
        }

        // =========================
        // 가이드 박스 (중심 기준)
        // =========================
        private void DrawGuideBoxes(Graphics g)
        {
            if (clickState != ClickState.TwoPoints)
                return;

            int cropSize = (int)numCropSize.Value;
            if (cropSize <= 0)
                return;

            float dx = secondOriginalPt.X - firstOriginalPt.X;
            float dy = secondOriginalPt.Y - firstOriginalPt.Y;
            float length = (float)Math.Sqrt(dx * dx + dy * dy);
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

                    Point centerView = OriginalToView(new PointF(cx, cy));
                    int halfView = (int)((cropSize / 2f) / scaleX);

                    Rectangle rect = new Rectangle(
                        centerView.X - halfView,
                        centerView.Y - halfView,
                        halfView * 2,
                        halfView * 2);

                    g.DrawRectangle(pen, rect);
                }
            }
        }

        // =========================
        // 선 길이 / Crop Count
        // =========================
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

        // =========================
        // 유틸
        // =========================
        private bool IsHit(Point p, Point target)
        {
            return Math.Abs(p.X - target.X) <= HitRadius &&
                   Math.Abs(p.Y - target.Y) <= HitRadius;
        }

        private void DrawPoint(Graphics g, Point pt)
        {
            g.FillEllipse(
                Brushes.Red,
                pt.X - 4, pt.Y - 4,
                8, 8);
        }

        private void ResetAll()
        {
            clickState = ClickState.None;
            firstViewPt = Point.Empty;
            secondViewPt = Point.Empty;

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
            if (length <= 0)
                return;

            float ux = dx / length;
            float uy = dy / length;

            // Crop Count와 저장 개수 완전히 통일
            int cropTotal = (int)Math.Ceiling(length / cropSize);

            // 🔹 타임스탬프 폴더
            string timeStamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string folder = Path.Combine(Application.StartupPath, "Crops", timeStamp);
            Directory.CreateDirectory(folder);

            int fileIndex = 1; // ⭐ 001부터 시작

            for (int i = 0; i < cropTotal; i++)
            {
                float dist = i * cropSize;

                // ⭐ 마지막은 무조건 length
                if (dist > length)
                    dist = length;

                CropOne(dist, ux, uy, cropSize, folder, ref fileIndex);
            }

            MessageBox.Show($"Crop 저장 완료 ({fileIndex - 1}개)");
        }


        private void CropOne(
            float dist,
            float ux,
            float uy,
            int cropSize,
            string folder,
            ref int index)
        {
            float cx = firstOriginalPt.X + ux * dist;
            float cy = firstOriginalPt.Y + uy * dist;

            int x = (int)Math.Round(cx - cropSize / 2f);
            int y = (int)Math.Round(cy - cropSize / 2f);

            // 위치 clamp
            if (x < 0) x = 0;
            if (y < 0) y = 0;

            int width = cropSize;
            int height = cropSize;

            // 크기 clamp (핵심)
            if (x + width > originalMat.Width)
                width = originalMat.Width - x;

            if (y + height > originalMat.Height)
                height = originalMat.Height - y;

            if (width <= 0 || height <= 0)
                return;

            OpenCvSharp.Rect roi = new OpenCvSharp.Rect(x, y, width, height);

            using (OpenCvSharp.Mat cropped = new OpenCvSharp.Mat(originalMat, roi))
            {
                string path = Path.Combine(folder, $"crop_{index:D3}.png");
                OpenCvSharp.Cv2.ImWrite(path, cropped);
                index++;
            }
        }


        private Rectangle GetImageViewRect()
        {
            if (viewBitmap == null)
                return Rectangle.Empty;

            float imgRatio = (float)viewBitmap.Width / viewBitmap.Height;
            float boxRatio = (float)pictureBoxImage.Width / pictureBoxImage.Height;

            if (imgRatio > boxRatio)
            {
                int w = pictureBoxImage.Width;
                int h = (int)(w / imgRatio);
                int y = (pictureBoxImage.Height - h) / 2;
                return new Rectangle(0, y, w, h);
            }
            else
            {


                int h = pictureBoxImage.Height;
                int w = (int)(h * imgRatio);
                int x = (pictureBoxImage.Width - w) / 2;
                return new Rectangle(x, 0, w, h);
            }
        }


        private void btnCropSave_Click(object sender, EventArgs e)
        {
            CropAndSaveAll();
        }
    }
}
