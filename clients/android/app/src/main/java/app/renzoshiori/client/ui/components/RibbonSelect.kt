package app.renzoshiori.client.ui.components

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.ExpandMore
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.unit.dp
import app.renzoshiori.client.ui.theme.RenzoColors

data class SelectOption(
    val value: String,
    val label: String,
    /** Colored status dot before the label (web's status Select rows). */
    val dotColor: Color? = null,
    /** Muted count after the label (web's live count badges). */
    val count: Int? = null,
)

/**
 * The web app's Select control (ui/select.tsx SelectTrigger), transliterated:
 * a 32dp-high rounded-lg bordered trigger showing the selected option (with
 * its status dot), a chevron, and a Popover-style dark menu of options with
 * dots and muted counts — the Library ribbon's filter/sort controls.
 */
@Composable
fun RibbonSelect(
    options: List<SelectOption>,
    value: String,
    onChange: (String) -> Unit,
    modifier: Modifier = Modifier,
) {
    var open by remember { mutableStateOf(false) }
    val selected = options.firstOrNull { it.value == value } ?: options.firstOrNull()

    Box(modifier = modifier) {
        Row(
            verticalAlignment = Alignment.CenterVertically,
            modifier = Modifier
                .height(32.dp)
                .clip(RoundedCornerShape(8.dp))
                .border(1.dp, RenzoColors.Border, RoundedCornerShape(8.dp))
                .background(RenzoColors.Background)
                .clickable { open = true }
                .padding(horizontal = 10.dp),
        ) {
            if (selected?.dotColor != null) {
                Box(
                    modifier = Modifier
                        .padding(end = 6.dp)
                        .size(6.dp)
                        .clip(CircleShape)
                        .background(selected.dotColor),
                )
            }
            Text(
                selected?.label ?: "",
                style = MaterialTheme.typography.labelMedium,
                color = RenzoColors.Foreground,
            )
            Icon(
                Icons.Filled.ExpandMore,
                contentDescription = null,
                tint = RenzoColors.MutedForeground,
                modifier = Modifier.padding(start = 4.dp).size(16.dp),
            )
        }
        DropdownMenu(
            expanded = open,
            onDismissRequest = { open = false },
            containerColor = RenzoColors.Popover,
        ) {
            options.forEach { opt ->
                DropdownMenuItem(
                    text = {
                        Row(verticalAlignment = Alignment.CenterVertically) {
                            if (opt.dotColor != null) {
                                Box(
                                    modifier = Modifier
                                        .padding(end = 8.dp)
                                        .size(6.dp)
                                        .clip(CircleShape)
                                        .background(opt.dotColor),
                                )
                            }
                            Text(
                                opt.label,
                                style = MaterialTheme.typography.bodyMedium,
                                color = RenzoColors.Foreground,
                            )
                            if (opt.count != null && opt.count > 0) {
                                Text(
                                    opt.count.toString(),
                                    style = MaterialTheme.typography.labelSmall,
                                    color = Color.White.copy(alpha = 0.4f),
                                    modifier = Modifier.padding(start = 8.dp),
                                )
                            }
                        }
                    },
                    onClick = {
                        onChange(opt.value)
                        open = false
                    },
                )
            }
        }
    }
}
