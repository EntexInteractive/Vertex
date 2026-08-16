import {LayoutDashboard, type LucideIcon} from "lucide-react";

type NavItem = {
    id: string;
    label: string;
    icon: LucideIcon;
    href: string;
};

export const navItems: { label: string; items: NavItem[] }[] = [
    {
        label: "Repository",
        items: [
            {
                "id": "dashboard",
                "label": "Dashboard",
                "icon": LayoutDashboard,
                "href": "/"
            }
        ]
    }
]