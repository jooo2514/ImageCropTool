using System.Drawing;

namespace ImageCropTool
{
    public class GuideLineInfo
    {
        public PointF StartPt;
        public PointF EndPt;
        public int CropSize;
        public CropAnchor Anchor;

        public GuideLineInfo()
        {
            StartPt = PointF.Empty;
            EndPt = PointF.Empty;
            CropSize = 0;
            Anchor = CropAnchor.Center;
        }
    }
}
