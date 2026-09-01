using System;
using UnityEngine;

namespace CsvTool.Editor
{
    /// <summary>
    /// Reusable field-form projection for the record selected by a CsvRecordView.
    /// Record browsing remains the responsibility of the full view or its host.
    /// </summary>
    public sealed class CsvRecordForm
    {
        private readonly CsvRecordView recordView;

        internal CsvRecordForm(CsvRecordView recordView)
        {
            this.recordView = recordView ?? throw new ArgumentNullException("recordView");
        }

        public CsvRecordView RecordView { get { return recordView; } }
        public int SelectedRecordIndex { get { return recordView.SelectedRecordIndex; } }

        /// <summary>Draws grouped editable fields without a record list or previous/next controls.</summary>
        public void Draw(Rect rect)
        {
            recordView.DrawForm(rect);
        }
    }
}
