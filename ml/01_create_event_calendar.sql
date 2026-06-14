-- ============================================================
-- STEP 1: Event Calendar for FUTURE planned events
-- Run this in pgAdmin 4 (Query Tool on parking_db)
--
-- WHY: the model cannot know tomorrow has an event unless you
-- tell it. Malls plan events in advance, so we store them here.
-- Past event days are already in Event_Log_Table; this table is
-- for upcoming ones.
-- ============================================================

CREATE TABLE IF NOT EXISTS "Event_Calendar" (
    "Event_Date"     date PRIMARY KEY,
    "Event_Name"     text NOT NULL,
    "Expected_Scale" text NOT NULL DEFAULT 'Medium'  -- Small / Medium / Large
);

-- Sample upcoming events (EDIT THESE — put real/realistic events
-- for your demo period; dates must be in the future)
INSERT INTO "Event_Calendar" ("Event_Date", "Event_Name", "Expected_Scale") VALUES
    ('2026-06-13', 'Mega Sale Carnival Weekend', 'Large'),
    ('2026-06-14', 'Mega Sale Carnival Weekend', 'Large'),
    ('2026-06-27', 'Hari Raya Haji Shopping Fair', 'Medium'),
    ('2026-07-04', 'KL Food Festival', 'Medium'),
    ('2026-07-05', 'KL Food Festival', 'Medium')
ON CONFLICT ("Event_Date") DO NOTHING;

-- Check it worked:
SELECT * FROM "Event_Calendar" ORDER BY "Event_Date";
