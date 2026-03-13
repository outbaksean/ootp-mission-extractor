using mission_extractor.Models;

namespace mission_extractor.Services
{
    public class MissionBoundryService
    {

        private readonly MissionRowBoundries _missionRowBoundries;

        public MissionBoundryService(MissionRowBoundries missionRowBoundries)
        {
            _missionRowBoundries = missionRowBoundries;
        }

        public CaptureRegionConfig GetCategory(int rowIndex)
        {
            if (rowIndex < 0 || rowIndex >= _missionRowBoundries.NumRows)
            {
                throw new ArgumentOutOfRangeException(nameof(rowIndex), "Row index is out of range.");
            }
            return new CaptureRegionConfig
            {
                Left = _missionRowBoundries.CategoryLeft,
                Top = _missionRowBoundries.TopRow + _missionRowBoundries.TopRowOffset + (rowIndex * _missionRowBoundries.RowHeight),
                Width = _missionRowBoundries.CategoryRight - _missionRowBoundries.CategoryLeft,
                Height = _missionRowBoundries.RowHeight
            };
        }

        public CaptureRegionConfig GetTitle(int rowIndex)
        {
            if (rowIndex < 0 || rowIndex >= _missionRowBoundries.NumRows)
            {
                throw new ArgumentOutOfRangeException(nameof(rowIndex), "Row index is out of range.");
            }
            return new CaptureRegionConfig
            {
                Left = _missionRowBoundries.TitleLeft,
                Top = _missionRowBoundries.TopRow + _missionRowBoundries.TopRowOffset + (rowIndex * _missionRowBoundries.RowHeight),
                Width = _missionRowBoundries.TitleRight - _missionRowBoundries.TitleLeft,
                Height = _missionRowBoundries.RowHeight
            };
        }

        public CaptureRegionConfig GetReward(int rowIndex)
        {
            if (rowIndex < 0 || rowIndex >= _missionRowBoundries.NumRows)
            {
                throw new ArgumentOutOfRangeException(nameof(rowIndex), "Row index is out of range.");
            }
            return new CaptureRegionConfig
            {
                Left = _missionRowBoundries.RewardLeft,
                Top = _missionRowBoundries.TopRow + _missionRowBoundries.TopRowOffset + (rowIndex * _missionRowBoundries.RowHeight),
                Width = _missionRowBoundries.RewardRight - _missionRowBoundries.RewardLeft,
                Height = _missionRowBoundries.RowHeight
            };
        }

        public CaptureRegionConfig GetStatus(int rowIndex)
        {
            if (rowIndex < 0 || rowIndex >= _missionRowBoundries.NumRows)
            {
                throw new ArgumentOutOfRangeException(nameof(rowIndex), "Row index is out of range.");
            }
            return new CaptureRegionConfig
            {
                Left = _missionRowBoundries.StatusLeft,
                Top = _missionRowBoundries.TopRow + _missionRowBoundries.TopRowOffset + (rowIndex * _missionRowBoundries.RowHeight),
                Width = _missionRowBoundries.StatusRight - _missionRowBoundries.StatusLeft,
                Height = _missionRowBoundries.RowHeight
            };
        }

        public CaptureRegionConfig GetDetail(int row, int column, bool useLowerOffset = false, int captureRow = 0, bool noImageOffsets = false)
        {
            if (column < 0 || column >= _missionRowBoundries.DetailColumns)
            {
                throw new ArgumentOutOfRangeException(nameof(column), "Column index is out of range.");
            }
            int captureRowOffset = captureRow * _missionRowBoundries.RowHeight;
            int detailUpperOffsetY = noImageOffsets ? 0 : _missionRowBoundries.DetailUpperOffsetY;
            int detailSkipY = noImageOffsets ? 0 : _missionRowBoundries.DetailSkipY;
            int detailHeight = noImageOffsets ? _missionRowBoundries.DetailNoImagesHeight : _missionRowBoundries.DetailHeight;
            var offset = _missionRowBoundries.TopRow + _missionRowBoundries.RowHeight + detailUpperOffsetY + captureRowOffset;
            if (useLowerOffset)
            {
                offset = _missionRowBoundries.TopRow + _missionRowBoundries.DetailLowerOffsetY + captureRowOffset;
            }
            int top = offset + row * (detailHeight + detailSkipY);
            return new CaptureRegionConfig
            {
                Left = _missionRowBoundries.DetailLeft + (column * _missionRowBoundries.DetailWidth),
                Top = top,
                Width = _missionRowBoundries.DetailWidth,
                Height = detailHeight
            };
        }

        public CaptureRegionConfig GetMissionTypeDetail(int rowIndex, int captureRow = 0)
        {
            int captureRowOffset = captureRow * _missionRowBoundries.RowHeight;
            var region = new CaptureRegionConfig
            {
                Left = _missionRowBoundries.CategoryLeft,
                Top = _missionRowBoundries.TopRow + (_missionRowBoundries.RowHeight * 2) + (rowIndex * _missionRowBoundries.RowHeight) + captureRowOffset,
                Width = (int)((_missionRowBoundries.CategoryLeft + _missionRowBoundries.CategoryRight) * 1.4),
                Height = _missionRowBoundries.RowHeight
            };
            Console.WriteLine($"RowIndex: {rowIndex}, Left: {region.Left}, Top: {region.Top}, Width: {region.Width}, Height: {region.Height}");

            return region;
        }

        public int MaxRowIndex => _missionRowBoundries.NumRows - 1;
        public int MaxColumnIndex => _missionRowBoundries.DetailColumns - 1;
    }
}
