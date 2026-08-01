package app.renzoshiori.client.ui.components

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.widthIn
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
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import app.renzoshiori.client.ui.theme.RenzoColors

data class SelectOption(
    val value: String,
    val label: String,
    /** Colored status dot before the label (web's status Select rows). */
    val dotColor: Color? = null,
    /** Muted count after the label (web's live count badges). */
    val count: Int? = null,
    /** Renders the web's "└" prefix for a favourites sub-list. */
    val indented: Boolean = false,
    /** Leading glyph (the Browse source picker's globe / language flag stand-in). */
    val icon: ImageVector? = null,
)

/**
 * The web app's Select control (ui/select.tsx SelectTrigger), transliterated:
 * a 32dp-high rounded-lg bordered trigger showing the selected option (with
 * its status dot), a chevron, and a Popover-style dark menu of options with
 * dots and muted counts — the Library/Browse ribbon's filter/sort controls.
 */
@Composable
fun RibbonSelect(
    options: List<SelectOption>,
    value: String,
    onChange: (String) -> Unit,
    modifier: Modifier = Modifier,
    /** Shown when `value` matches no option (web's SelectValue placeholder). */
    placeholder: String? = null,
    /** Caps the trigger label so a long genre/source name can't stretch the ribbon. */
    maxTriggerWidth: androidx.compose.ui.unit.Dp = 148.dp,
) {
    var open by remember { mutableStateOf(false) }
    val selected = options.firstOrNull { it.value == value }

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
            if (selected?.icon != null) {
                Icon(
                    selected.icon,
                    contentDescription = null,
                    tint = RenzoColors.MutedForeground,
                    modifier = Modifier.padding(end = 6.dp).size(14.dp),
                )
            }
            Text(
                selected?.label ?: placeholder ?: options.firstOrNull()?.label.orEmpty(),
                style = MaterialTheme.typography.labelMedium,
                color = if (selected == null && placeholder != null) RenzoColors.MutedForeground else RenzoColors.Foreground,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
                modifier = Modifier.widthIn(max = maxTriggerWidth),
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
            modifier = Modifier.heightIn(max = 420.dp),
        ) {
            options.forEach { opt ->
                DropdownMenuItem(
                    text = {
                        Row(verticalAlignment = Alignment.CenterVertically) {
                            if (opt.indented) {
                                Text(
                                    "└",
                                    style = MaterialTheme.typography.bodyMedium,
                                    color = RenzoColors.MutedForeground.copy(alpha = 0.6f),
                                    modifier = Modifier.padding(end = 6.dp),
                                )
                            }
                            if (opt.dotColor != null) {
                                Box(
                                    modifier = Modifier
                                        .padding(end = 8.dp)
                                        .size(6.dp)
                                        .clip(CircleShape)
                                        .background(opt.dotColor),
                                )
                            }
                            if (opt.icon != null) {
                                Icon(
                                    opt.icon,
                                    contentDescription = null,
                                    tint = RenzoColors.MutedForeground,
                                    modifier = Modifier.padding(end = 8.dp).size(16.dp),
                                )
                            }
                            Text(
                                opt.label,
                                style = MaterialTheme.typography.bodyMedium,
                                color = RenzoColors.Foreground,
                                maxLines = 1,
                                overflow = TextOverflow.Ellipsis,
                                modifier = Modifier.widthIn(max = 240.dp),
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

/**
 * The web ribbon's small pill toggle — "My library" / "All libraries",
 * "Track all". Border + faint fill when off, primary-tinted when on. Never
 * dimmed: the callers hide it entirely when it doesn't apply.
 */
@Composable
fun RibbonToggleChip(
    label: String,
    active: Boolean,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    icon: ImageVector? = null,
) {
    Row(
        verticalAlignment = Alignment.CenterVertically,
        modifier = modifier
            .height(32.dp)
            .clip(RoundedCornerShape(50))
            .background(
                if (active) RenzoColors.Primary.copy(alpha = 0.15f)
                else RenzoColors.Foreground.copy(alpha = 0.04f),
            )
            .border(
                1.dp,
                if (active) RenzoColors.Primary.copy(alpha = 0.40f) else RenzoColors.Border.copy(alpha = 0.40f),
                RoundedCornerShape(50),
            )
            .clickable(onClick = onClick)
            .padding(horizontal = 12.dp),
    ) {
        if (icon != null) {
            Icon(
                icon,
                contentDescription = null,
                tint = if (active) RenzoColors.Primary else RenzoColors.MutedForeground,
                modifier = Modifier.padding(end = 6.dp).size(14.dp),
            )
        }
        Text(
            label,
            style = MaterialTheme.typography.labelMedium,
            color = if (active) RenzoColors.Primary else RenzoColors.MutedForeground,
            maxLines = 1,
        )
    }
}

/**
 * The Queue ribbon's segmented control (queue/page.tsx FilterPills): a rounded
 * track with hairline border, the active pill filled with the primary color
 * and primary-foreground text, inactive pills muted.
 */
@Composable
fun SegmentedPills(
    labels: List<String>,
    selectedIndex: Int,
    onSelect: (Int) -> Unit,
    modifier: Modifier = Modifier,
) {
    Row(
        verticalAlignment = Alignment.CenterVertically,
        modifier = modifier
            .clip(RoundedCornerShape(50))
            .border(1.dp, RenzoColors.Foreground.copy(alpha = 0.06f), RoundedCornerShape(50))
            .background(RenzoColors.Foreground.copy(alpha = 0.015f))
            .padding(2.dp),
    ) {
        labels.forEachIndexed { index, label ->
            val isActive = index == selectedIndex
            if (index > 0) Spacer(Modifier.size(2.dp))
            Box(
                contentAlignment = Alignment.Center,
                modifier = Modifier
                    .clip(RoundedCornerShape(50))
                    .background(if (isActive) RenzoColors.Primary else Color.Transparent)
                    .clickable { onSelect(index) }
                    .padding(horizontal = 14.dp, vertical = 5.dp),
            ) {
                Text(
                    label,
                    style = MaterialTheme.typography.labelMedium,
                    color = if (isActive) RenzoColors.PrimaryForeground else RenzoColors.MutedForeground,
                    maxLines = 1,
                )
            }
        }
    }
}
