# Smart Thumbnail Crop - YOLOv8n ONNX Model

## Setup

Place the `yolov8n.onnx` model file in this directory.

### How to obtain the model:

**Option 1: Export from Ultralytics (recommended)**
```bash
pip install ultralytics
python -c "from ultralytics import YOLO; model = YOLO('yolov8n.pt'); model.export(format='onnx', imgsz=640, simplify=True)"
```

**Option 2: Download pre-exported**
- Download from: https://github.com/ultralytics/assets/releases
- File: `yolov8n.onnx` (~6.3MB)

## Model Details

- **Architecture**: YOLOv8 Nano (smallest variant)
- **Input**: 640x640 RGB image (NCHW format)
- **Output**: [1, 84, 8400] tensor (4 bbox + 80 class scores × 8400 predictions)
- **Size**: ~6.3MB
- **Inference**: ~5-15ms on modern CPU
- **Classes**: 80 COCO classes (person, car, etc.)

## How it works

The smart crop feature:
1. Runs YOLOv8n inference on the thumbnail
2. Detects the main subject (prioritizes "person" class)
3. Crops the square region centered on the detected subject
4. Falls back to center/left crop if no subject detected

This ensures music thumbnails (especially YouTube 16:9) are cropped
to show the artist/main content rather than arbitrary center cropping.
