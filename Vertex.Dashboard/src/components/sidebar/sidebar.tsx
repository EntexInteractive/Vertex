import {useState} from "react";
import {NavLink} from "react-router-dom";
import {Menu} from "lucide-react";
import {navItems} from "./nav-links.ts";

export function Sidebar() {
    const [isMobileOpen, setIsMobileOpen] = useState(false);

    return (
        <>
            <button
                className="sidebar-mobile-toggle"
                onClick={() => setIsMobileOpen(!isMobileOpen)}
                aria-label="Toggle sidebar"
            >
                <Menu size={24} />
            </button>

            <aside className={`sidebar ${isMobileOpen ? "sidebar--open" : ""}`}>
                <header className="sidebar-header">
                    <div className="sidebar-header-content">
                        <span className="sidebar-title">Vertex</span>
                    </div>
                </header>

                <nav className="sidebar-nav" aria-label="Primary navigation">
                    {navItems.map((group) => (
                        <section
                            className="sidebar-nav-group"
                            key={group.label}
                        >
                            <h2 className="sidebar-nav-label">
                                {group.label}
                            </h2>

                            {group.items.map((item) => {
                                const Icon = item.icon;

                                return (
                                    <NavLink
                                        key={item.id}
                                        to={item.href}
                                        onClick={() => setIsMobileOpen(false)}
                                        className={({isActive}) =>
                                            `sidebar-link ${
                                                isActive
                                                    ? "sidebar-link--active"
                                                    : ""
                                            }`
                                        }
                                    >
                                        <Icon size={18} />
                                        <span>{item.label}</span>
                                    </NavLink>
                                );
                            })}
                        </section>
                    ))}
                </nav>

                <footer className="sidebar-footer">
                    <button className="sidebar-signout">
                        <span aria-hidden="true">↪</span>
                        <span>Sign Out</span>
                    </button>
                </footer>
            </aside>
        </>
    );
}