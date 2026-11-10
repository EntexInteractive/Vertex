import {Home, Settings} from "lucide-react";

interface SidebarProps {
    currentPage: string;
    onPageChange: (page: string) => void;
}

export function Sidebar({currentPage, onPageChange}: SidebarProps) {
    const sections = [
        {id: "home", label: "Home", icon: Home, badge: 2}
    ];

    return (
        <nav className="rail">
            <div className="vertex-mark">
                <span>V</span>
            </div>
            {sections.map((item) => {
                const isActive = currentPage === item.id;
                const Icon = item.icon;

                return (
                    <button
                        key={item.id}
                        className={isActive ? "active" : ""}
                        onClick={() => onPageChange(item.id)}
                        title={item.label}
                    >
                        <Icon />
                        {item.badge && <b>{item.badge}</b>}
                        <span>{item.label}</span>
                    </button>
                )
            })}
            <div className="rail-spacer" />
            <button title="Settings">
                <Settings/>
                <span>Settings</span>
            </button>
        </nav>

/*        <div className="w-20 h-full bg-sidebar border-r border-border flex flex-col items-center">
            <div className="py-6 border-b border-border w-full flex justify-center">
                <img src="/src/assets/vertex-icon.svg" className="w-15 h-15" alt="Vertex Desktop"/>
            </div>

            <nav className="flex-1 py-4 flex flex-col gap-2 w-full px-3">
                {/!* .map() is used to loop through arrays in React *!/}
                {navItems.map((item) => {
                    const Icon = item.icon;
                    const isActive = currentPage === item.id;

                    return (
                        <button
                            key={item.id} // Every item in a list needs a unique "key"
                            onClick={() => onPageChange(item.id)} // When clicked, change the page
                            title={item.label} // Tooltip on hover
                            className={`w-full h-14 flex items-center justify-center rounded-lg transition-colors duration-200
                            ${ isActive ? "bg-primary text-primary-foreground" : "hover:bg-accent text-foreground" }`}>
                            <Icon className="w-6 h-6"/>
                        </button>
                    );
                })}
            </nav>

            {/!* Settings at Bottom *!/}
            <div className="py-4 border-t border-border w-full px-3">
                <button
                    onClick={() => onPageChange("settings")}
                    title="Settings"
                    className={`
            w-full h-14 flex items-center justify-center rounded-lg
            transition-colors duration-200
            ${
                        currentPage === "settings"
                            ? "bg-primary text-primary-foreground"
                            : "hover:bg-accent text-foreground"
                    }
          `}
                >
                    <Settings className="w-6 h-6"/>
                </button>
            </div>
        </div>*/
    );
}