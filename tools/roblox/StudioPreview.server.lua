-- Preview converted PaRappa KeyframeSequences on an R15 rig in Roblox Studio,
-- without uploading anything.
--
-- Setup:
--   1. Avatar tab > Rig Builder > Block Rig (R15).
--   2. Drag one or more .rbxmx files from outputs/roblox-r15 into the Studio viewport.
--   3. Put this script in ServerScriptService and press Run (F8).
--
-- Every KeyframeSequence in Workspace or ServerStorage plays in turn; the
-- name of the current one is printed to the Output window.
-- RegisterKeyframeSequence only works in Studio. Use "Save to Roblox" on a
-- KeyframeSequence to get an ID for a live game.

local KeyframeSequenceProvider = game:GetService("KeyframeSequenceProvider")
local ServerStorage = game:GetService("ServerStorage")

local SECONDS_PER_ANIMATION = 4

-- Use the first R15 character in Workspace, whatever it is called.
local humanoid
for _, item in ipairs(workspace:GetDescendants()) do
	if item:IsA("Humanoid") and item.RigType == Enum.HumanoidRigType.R15 then
		humanoid = item
		break
	end
end
if not humanoid then
	warn("No R15 rig in Workspace - add one with Avatar > Rig Builder > Block Rig (R15)")
	return
end
local rig = humanoid.Parent
print("Previewing on", rig:GetFullName())
local animator = humanoid:FindFirstChildOfClass("Animator") or Instance.new("Animator", humanoid)
local root = rig:FindFirstChild("HumanoidRootPart")
if root then
	root.Anchored = true -- keep the dummy standing still while previewing
end

local sequences = {}
for _, container in ipairs({ workspace, ServerStorage }) do
	for _, item in ipairs(container:GetDescendants()) do
		if item:IsA("KeyframeSequence") then
			table.insert(sequences, item)
		end
	end
end
table.sort(sequences, function(a, b)
	return a.Name < b.Name
end)
if #sequences == 0 then
	warn("No KeyframeSequence found - drag a .rbxmx file from outputs/roblox-r15 into Studio first")
	return
end

local tracks = {}
for _, sequence in ipairs(sequences) do
	local animation = Instance.new("Animation")
	animation.AnimationId = KeyframeSequenceProvider:RegisterKeyframeSequence(sequence)
	local track = animator:LoadAnimation(animation)
	track.Looped = true
	table.insert(tracks, { name = sequence.Name, track = track })
end

while true do
	for _, entry in ipairs(tracks) do
		print("Playing", entry.name)
		entry.track:Play(0.1)
		task.wait(SECONDS_PER_ANIMATION)
		entry.track:Stop(0.1)
	end
end
