using System.Drawing;

namespace ImageCropTool
{
    public class CropBoxInfo
    {
        public Rectangle Rect;
        public bool IsHovered;
        public GuideLineInfo OwnerLine;
        public GuideLineInfo ParentLine;

        public CropBoxInfo()
        {
            Rect = Rectangle.Empty;
            IsHovered = false;
        }
    }
}
