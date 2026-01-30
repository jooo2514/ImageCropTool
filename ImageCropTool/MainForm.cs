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
         *  고정 설정값
         * ========================================================= */ 

        // 기본 크롭 사이즈 (px)
        private const int DefaultCropSize = 512;

        // 점 드래그 판정을 위한 클릭 반경
        private const int HitRadius = 8;


        /* =========================================================
         *  이미지 관련 필드 
         * ========================================================= */

        // 실제 원본 이미지 (크롭 기준)
        private Bitmap originalBitmap;

        // OpenCV 처리용 Mat
        private Mat originalMat;

        // PictureBox에 표시되는 축소 이미지
        private Bitmap viewBitmap;


        /* =========================================================
         *  상태 관리 변수
         * ========================================================= */

        // 이미지 로딩 중 여부
        private bool isImageLoading = false;

        // 로딩 스피너용 타이머
        private Timer loadingTimer;

        // 스피너 회전 시작 각도
        private float spinnerAngle = 0f;


        /* =========================================================
         *  클릭 상태 관리 (상태 머신)
         * ========================================================= */

        private enum ClickState
        {
            None,       // 점 없음
            OnePoint,   // 첫 번째 점만 있음
            TwoPoints   // 두 점 모두 있음
        }

        private ClickState clickState = ClickState.None;   // 초기 상태


        /* =========================================================
         *  드래그 관련 상태    
         * ========================================================= */

        private bool isDragging = false;
        private bool draggingFirst = false;
        private bool draggingSecond = false;

        // 잘못된 클릭 안내 중복 방지
        private bool invalidClickNotified = false;


        /* =========================================================
         *  좌표 정보
         * ========================================================= */

        // View 좌표 (PictureBox 기준)
        private DPoint firstViewPt;
        private DPoint secondViewPt;

        // Original 좌표 (실제 이미지 기준)
        private PointF firstOriginalPt;
        private PointF secondOriginalPt;


        /* =========================================================
         *  계산 결과 정보
         * ========================================================= */

        private float lineLength = 0f;
        private int cropCount = 0;

        /* =========================================================
         *  생성자
         * ========================================================= */
        public MainForm()
        {
            InitializeComponent();

            // 로딩 스피너 타이머 초기화
            loadingTimer = new Timer
            {
                Interval = 50      // 50ms마다 한 번씩 Tick 발생(1초에 약 20번)
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
            //pictureBoxImage.MouseWheel += pictureBoxImage_MouseWheel;

            // 기본 크롭 사이즈 설정
            numCropSize.Value = DefaultCropSize;

        }


        /* =========================================================
         *  초기화 관련
         * ========================================================= */
        private void ClearPoints()   // 점과 선만 초기화
        {
            clickState = ClickState.None;  // 상태 머신 초기화

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
         *  이미지 로딩
         * ========================================================= */
      
        private async void btnLoadImage_Click(object sender, EventArgs e)
        {
            OpenFileDialog dlg = new OpenFileDialog
            {
                Filter = "Image Files|*.bmp;*.jpg;*.png"
            };

            if (dlg.ShowDialog() != DialogResult.OK)
                return;

            // UI 잠금 + 로딩 시작
            isImageLoading = true;
            loadingTimer.Start();

            pictureBoxImage.Enabled = false;   // pictureBox 컨트롤 비활성화(클릭/드래그 못함)
            btnCropSave.Enabled = false;       // 저장 버튼 비활성화
            btnLoadImage.Enabled = false;      // 이미지 불러오기 버튼 비활성화
            btnReset.Enabled = false;

            try
            {
                Bitmap preview = null;

                // View 이미지 생성 (비동기)
                await Task.Run(() =>
                {
                    using (Bitmap full = new Bitmap(dlg.FileName))
                    {
                        preview = ResizeToFit(  // picturebox 크기에 맞게 비율 유지 축소
                            full,
                            pictureBoxImage.Width,
                            pictureBoxImage.Height
                        );
                    }
                });

                viewBitmap?.Dispose();    // 이전에 남아 있는게 있으면 정리
                viewBitmap = preview;   // 화면 표시 전용 축소된 Bitmap
                pictureBoxImage.Image = viewBitmap;   // pictureBox가 화면에 렌더링

                // 원본 이미지 & Mat 로드
                await Task.Run(() =>
                {
                    originalBitmap?.Dispose();
                    originalMat?.Dispose();

                    originalBitmap = new Bitmap(dlg.FileName);   // 좌표용
                    originalMat = BitmapConverter.ToMat(originalBitmap);  // openCV 이미지 처리용
                });

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

            DRect imageRect = GetImageViewRect();  // 이미지 그려질 영역 저장

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

            // 기존 점 드래그 판정
            if (clickState != ClickState.None)
            {
                if (IsHit(e.Location, firstViewPt))   //  true or false
                {
                    isDragging = true;
                    draggingFirst = true;
                    return;
                }
                // 두번째 점 드래그 판정
                if (clickState == ClickState.TwoPoints &&
                    IsHit(e.Location, secondViewPt))
                {
                    isDragging = true;
                    draggingSecond = true;
                    return;
                }
            }

            // 상태별 클릭 처리
            if (clickState == ClickState.None)   // 아무 점 없을 때 
            {
                firstViewPt = e.Location;                        // PictureBox 좌표
                firstOriginalPt = ViewToOriginal(firstViewPt);   // 원본 이미지 좌표
                clickState = ClickState.OnePoint;                // 상태를 OnePoint로 변경
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

                pictureBoxImage.Invalidate();
            }
        }
        private void pictureBoxImage_MouseMove(object sender, MouseEventArgs e)
        {
            if (!isDragging)
                return;

            if (draggingFirst)
            {
                firstViewPt = e.Location;   // 점 좌표 갱신
                firstOriginalPt = ViewToOriginal(firstViewPt);   // original 좌표 동기화


            }
            else if (draggingSecond)
            {
                secondViewPt = e.Location;
                secondOriginalPt = ViewToOriginal(secondViewPt);
            }

            UpdateLineInfo();   // 선 길이 계산
            pictureBoxImage.Invalidate();   // 화면 갱신
        }

        private void pictureBoxImage_MouseUp(object sender, MouseEventArgs e)   // 모든 드래그 플래그 해제
        {
            isDragging = false;
            draggingFirst = false;
            draggingSecond = false;
        }


        /* =========================================================
         *  그리기 (Paint)
         * ========================================================= */

        private void pictureBoxImage_Paint(object sender, PaintEventArgs e)  // 점,선 그리기
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
                    DrawPoint(e.Graphics, firstViewPt);    // 첫 점 그림

                if (clickState == ClickState.TwoPoints)
                {
                    DrawPoint(e.Graphics, secondViewPt);   // 두번째 점 그림
                    e.Graphics.DrawLine(pen, firstViewPt, secondViewPt);   // 선 그리기
                }
            }

            DrawGuideBoxes(e.Graphics); 
        }

        private void DrawGuideBoxes(Graphics g)
        {
            // 두 점이 모두 있어야 가이드 표시
            if (clickState != ClickState.TwoPoints)
                return;

            int cropSize = (int)numCropSize.Value;
            if (cropSize <= 0)
                return;

            // View에서 실제 이미지가 그려진 영역
            DRect viewRect = GetImageViewRect();

            if (viewRect.IsEmpty)
                return;

            // 원본 좌표 기준 선 방향 벡터
            float dx = secondOriginalPt.X - firstOriginalPt.X;
            float dy = secondOriginalPt.Y - firstOriginalPt.Y;

            float length = (float)Math.Sqrt(dx * dx + dy * dy);

            if (length < 1f)
                return;

            // 단위 방향 벡터
            float ux = dx / length;
            float uy = dy / length;

            // 원본 cropSize → View cropSize 변환
            float viewCropSize =
                cropSize * (float)viewRect.Width / originalBitmap.Width;

            int halfSize = (int)(viewCropSize / 2f);

            using (Pen pen = new Pen(Color.Yellow, 2))
            {
                pen.DashStyle = DashStyle.Dash;

                // 원본 기준 cropSize 간격으로 반복
                for (float dist = 0; dist <= length+(cropSize/2); dist += cropSize)
                {
                    // 원본 좌표 기준 박스 중심
                    PointF originalCenter = new PointF(
                        firstOriginalPt.X + ux * dist,
                        firstOriginalPt.Y + uy * dist
                    );

                    // View 좌표로 변환 (여백 포함)
                    DPoint viewCenter = OriginalToView(originalCenter);

                    g.DrawRectangle(
                        pen,
                        viewCenter.X ,
                        viewCenter.Y,
                        halfSize * 2,
                        halfSize * 2
                    );
                }
            }
        }


        /* =========================================================
         *  유틸리티 / 계산
         * ========================================================= */
        private void UpdateLineInfo()
        {
            if (clickState != ClickState.TwoPoints)
                return;

            float dx = secondOriginalPt.X - firstOriginalPt.X;
            float dy = secondOriginalPt.Y - firstOriginalPt.Y;

            lineLength = (float)Math.Sqrt(dx * dx + dy * dy);

            int cropSize = (int)numCropSize.Value;

            // 끝점 포함 기준 cropCount 계산
            cropCount = (int)Math.Floor(
                (lineLength + cropSize / 2f) / cropSize) + 1;

            lblLineLength.Text = $"Line Length: {lineLength:F1}px";
            lblCropCount.Text = $"Crop Count: {cropCount}";
        }


        private void numCropSize_ValueChanged(object sender, EventArgs e)
        {
            UpdateLineInfo();
            pictureBoxImage.Invalidate();
        }

        // 점 클릭 허용 반경(8px)
        private bool IsHit(DPoint p, DPoint target)   // p:마우스 위치  target:점의 위치
        {
            return Math.Abs(p.X - target.X) <= HitRadius &&     // Math.Abs(절대값)
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
         *  좌표 변환
         * ========================================================= */
        private PointF ViewToOriginal(DPoint pt)  // pt : 클릭좌표
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
         *  크롭 및 저장
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

            // 원본 좌표 기준 선 방향 벡터
            float dx = secondOriginalPt.X - firstOriginalPt.X;
            float dy = secondOriginalPt.Y - firstOriginalPt.Y;

            float length = (float)Math.Sqrt(dx * dx + dy * dy);

            if (length < 1f)
                return;

            // 단위 방향 벡터
            float ux = dx / length;
            float uy = dy / length;

            // 저장 폴더
            string folder = Path.Combine(
                Application.StartupPath,
                "Crops",
                DateTime.Now.ToString("yyyyMMdd_HHmmss")
            );
            Directory.CreateDirectory(folder);

            int index = 1;

            for (float dist = 0; dist <= length+(cropSize/2); dist += cropSize)  // 마지막 점이 박스안에 포함되도록
            {
                // 원본 기준 크롭 중심점
                float cx = firstOriginalPt.X + ux * dist;
                float cy = firstOriginalPt.Y + uy * dist;

                // 좌상단 좌표 (중심 → 좌상단)
                int x = (int)(cx - cropSize / 2f);
                int y = (int)(cy - cropSize / 2f);

                using (Mat cropped = new Mat(
                    originalMat,
                    new Rect(x, y, cropSize, cropSize)))
                {
                    Cv2.ImWrite(
                        Path.Combine(folder, $"crop_{index:D3}.png"),
                        cropped
                    );
                }

                index++;
            }

            MessageBox.Show($"저장 완료: {index - 1}개");
        }



        /* =========================================================
         *  기타 유틸
         * ========================================================= */
        private Bitmap ResizeToFit(Bitmap src, int maxW, int maxH)   // 이미지 비율에 맞춰 축소하고 비트맵에 복사
        {
            double scale = Math.Min(
                (double)maxW / src.Width,
                (double)maxH / src.Height
            );   // 가로 or 세로 중 비율 작은거 반환

            Bitmap dst = new Bitmap(         // 결과 크기 계산해서 도화지만듬
                (int)(src.Width * scale),
                (int)(src.Height * scale)
            );

            using (Graphics g = Graphics.FromImage(dst))  // 고품질 보간해서 이미지 그림
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(src, 0, 0, dst.Width, dst.Height);
            }

            return dst;
        } 


        private DRect GetImageViewRect()   // 이미지가 실제로 그려진 위치와 크기 반환
        {
            if (viewBitmap == null)
                return DRect.Empty;
               
            float imageRatio = (float)viewBitmap.Width / viewBitmap.Height;           // 이미지의 가로/세로 비율
            float boxRatio = (float)pictureBoxImage.Width / pictureBoxImage.Height;   // PictureBox의 가로/세로 비율

            if (imageRatio > boxRatio)  // 이미지가 더 가로로 긴 경우
            {
                int h = (int)(pictureBoxImage.Width / imageRatio);   // 세로 크기 계산

                return new DRect(
                    0,                                     // X (좌측)
                    (pictureBoxImage.Height - h) / 2,      // Y (위쪽 여백)
                    pictureBoxImage.Width,                 // 폭
                    h                                      // 높이
                );
            }
            else     // 이미지가 더 세로로 긴 경우
            {
                int w = (int)(pictureBoxImage.Height * imageRatio);  // 가로 크기 계산

                return new DRect(
                    (pictureBoxImage.Width - w) / 2,       // X (좌우 여백)
                    0,                                     // Y (위쪽)
                    w,                                     // 폭
                    pictureBoxImage.Height                 // 높이
                );
            }
        }

        private void DrawLoadingSpinner(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;

            int size = 50;  // 원의 지름

            // 중앙 좌표 계산
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
