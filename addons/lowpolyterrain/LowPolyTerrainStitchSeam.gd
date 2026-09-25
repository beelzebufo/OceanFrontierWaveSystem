@tool
extends Resource
class_name LowPolyTerrainStitchSeam

## Authoritative saved border shared by a LowPoly detail heightfield, MacroShell and future
## neighbouring detail/transition zones. Coordinates are stored in the owning manager's LOCAL
## space and ordered clockwise around the outer heightfield ring.

@export_storage var local_positions: PackedVector3Array = PackedVector3Array()
@export_storage var grid_size: Vector2i = Vector2i.ZERO
@export_storage var cell_size: float = 0.0
@export_storage var revision: int = 0


func clear() -> void:
	local_positions = PackedVector3Array()
	grid_size = Vector2i.ZERO
	cell_size = 0.0
	revision += 1
