-- Rename columns and update data types
ALTER TABLE server_stats 
    CHANGE COLUMN current_players player_count INT,
    CHANGE COLUMN max_slots server_slots INT;

-- Update existing records to have correct server slots (if any exist)
UPDATE server_stats SET server_slots = 10 WHERE server_slots = 64;
